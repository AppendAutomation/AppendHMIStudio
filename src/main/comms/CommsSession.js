// One renderer's connection to hmi-comms.
//
// The renderer never talks to the server itself -- the page's CSP forbids it
// and nothing in it should need to -- so the main process holds a WebSocket
// per window and relays. The server addresses tags by integer handle; this
// class keeps the handle-to-id map, so the renderer only ever sees its own
// tag ids. If the server restarts, the last configure and subscribe are
// replayed onto the new instance, so a running screen recovers by itself.

const REQUEST_TIMEOUT_MS = 30000;

export class CommsSession
{
	/**
	 * @param supervisor  a CommsSupervisor (ensureStarted, 'ready' events)
	 * @param emit        function(event) delivering pushes to the renderer
	 * @param opts        {WebSocket, setTimer, clearTimer} for tests
	 */
	constructor(supervisor, emit, opts)
	{
		opts = opts || {};
		this.supervisor = supervisor;
		this.emit = emit;
		this.WebSocket = opts.WebSocket || globalThis.WebSocket;
		this.setTimer = opts.setTimer || setTimeout;
		this.clearTimer = opts.clearTimer || clearTimeout;

		this.ws = null;
		this.opening = null;
		this.nextId = 1;
		this.pending = new Map();
		this.handleToId = new Map();
		this.lastConfigure = null;
		this.lastSubscribe = null;
		this.closed = false;
		this.serverPid = null;
		this.replaying = false;

		this.onReady = (info) =>
		{
			// A restarted server -- a different instance from the one this
			// session was talking to: reconnect and restore what the renderer
			// had. The first start also announces itself; that is not a restart.
			if (!this.closed && !this.replaying && this.lastConfigure != null && this.serverPid != null &&
				info != null && info.pid !== this.serverPid)
			{
				// Adopt the new instance at once, so the ready it announces while
				// the replay reconnects is not taken for yet another restart.
				this.serverPid = info.pid;
				this.replaying = true;

				this.replay().catch((e) =>
				{
					this.emit({t: 'server', state: 'failed', message: e.message});
				}).finally(() =>
				{
					this.replaying = false;
				});
			}
		};

		this.supervisor.on('ready', this.onReady);
	}

	/** Opens the socket and completes hello; shared by concurrent callers. */
	open()
	{
		if (this.ws != null && this.ws.readyState === 1)
		{
			return Promise.resolve();
		}

		if (this.opening == null)
		{
			this.opening = this.connect().finally(() =>
			{
				this.opening = null;
			});
		}

		return this.opening;
	}

	async connect()
	{
		const info = await this.supervisor.ensureStarted();
		this.serverPid = info.pid;

		await new Promise((resolve, reject) =>
		{
			const ws = new this.WebSocket('ws://127.0.0.1:' + info.port + '/v1');
			let settled = false;

			ws.onopen = () =>
			{
				this.ws = ws;
				this.send({t: 'hello', v: 1, token: info.token, client: 'append-hmi-studio'})
					.then((reply) =>
					{
						settled = true;

						if (reply.ok)
						{
							resolve();
						}
						else
						{
							reject(new Error('hmi-comms refused hello'));
						}
					}, (e) =>
					{
						settled = true;
						reject(e);
					});
			};

			ws.onmessage = (ev) => this.onMessage(ev.data);

			ws.onerror = () =>
			{
				if (!settled)
				{
					settled = true;
					reject(new Error('Cannot reach hmi-comms'));
				}
			};

			ws.onclose = () =>
			{
				if (this.ws === ws)
				{
					this.ws = null;
				}

				this.failPending(new Error('Connection to hmi-comms closed'));

				if (!settled)
				{
					settled = true;
					reject(new Error('hmi-comms closed the connection'));
				}
				else if (!this.closed)
				{
					this.emit({t: 'server', state: 'disconnected'});
				}
			};
		});
	}

	/** Sends a request; resolves with the reply carrying the same id. */
	send(msg)
	{
		const ws = this.ws;

		if (ws == null || ws.readyState !== 1)
		{
			return Promise.reject(new Error('Not connected to hmi-comms'));
		}

		const id = this.nextId++;
		msg = Object.assign({id: id}, msg);

		return new Promise((resolve, reject) =>
		{
			const timer = this.setTimer(() =>
			{
				this.pending.delete(id);
				reject(new Error('hmi-comms did not answer ' + msg.t));
			}, REQUEST_TIMEOUT_MS);

			this.pending.set(id, {resolve: resolve, reject: reject, timer: timer});
			ws.send(JSON.stringify(msg));
		});
	}

	failPending(error)
	{
		for (const p of this.pending.values())
		{
			this.clearTimer(p.timer);
			p.reject(error);
		}

		this.pending.clear();
	}

	onMessage(data)
	{
		let msg;

		try
		{
			msg = JSON.parse(data);
		}
		catch (e)
		{
			return;
		}

		if (msg.t === 'snapshot' || msg.t === 'change')
		{
			this.emit({t: msg.t, values: this.byId(msg.values)});

			return;
		}

		if (msg.t === 'status')
		{
			this.emit({t: 'status', devices: msg.devices});

			return;
		}

		if (msg.id != null && this.pending.has(msg.id))
		{
			const p = this.pending.get(msg.id);
			this.pending.delete(msg.id);
			this.clearTimer(p.timer);

			if (msg.t === 'error')
			{
				p.reject(new Error(msg.message || msg.code));
			}
			else
			{
				p.resolve(msg);
			}
		}
	}

	/** [[h, v, q, ts, status?, error?], ...] to {id: [v, q, ts, status?, error?]}. */
	byId(tuples)
	{
		const out = {};

		for (const t of tuples || [])
		{
			const id = this.handleToId.get(t[0]);

			if (id != null)
			{
				out[id] = t.slice(1);
			}
		}

		return out;
	}

	// ------------------------------------------------------------ requests

	async request(msg)
	{
		await this.open();

		return this.send(msg);
	}

	async configure(devices, tags)
	{
		this.lastConfigure = {devices: devices, tags: tags};
		this.lastSubscribe = null;
		const reply = await this.request({t: 'configure', devices: devices, tags: tags});
		this.handleToId = new Map(reply.tags.map((t) => [t.h, t.id]));

		return {
			tags: reply.tags.map((t) => ({id: t.id, normalized: t.normalized, error: t.error})),
			errors: reply.errors
		};
	}

	async subscribe(ids, rateMs, rates)
	{
		this.lastSubscribe = {ids: ids, rateMs: rateMs, rates: rates};
		const reply = await this.request({t: 'subscribe', tags: ids, rateMs: rateMs, rates: rates});

		return {ok: reply.ok, unknown: reply.unknown || []};
	}

	async unsubscribe()
	{
		this.lastSubscribe = null;
		await this.request({t: 'unsubscribe'});

		return {ok: true};
	}

	async read(ids)
	{
		const reply = await this.request({t: 'read', tags: ids});

		return {values: this.byId(reply.values), unknown: reply.unknown || []};
	}

	async write(values, timeoutMs)
	{
		const reply = await this.request({t: 'write', values: values, timeoutMs: timeoutMs});

		return {results: reply.results};
	}

	async validate(protocol, addresses, options, dataType)
	{
		const reply = await this.request({t: 'validate', protocol: protocol, addresses: addresses,
			options: options, dataType: dataType});

		return {results: reply.results};
	}

	async probe(device)
	{
		const reply = await this.request({t: 'probe', device: device});

		return {ok: reply.ok, error: reply.error, ms: reply.ms};
	}

	async status()
	{
		const reply = await this.request({t: 'status'});

		return {devices: reply.devices};
	}

	async diag()
	{
		const reply = await this.request({t: 'diag'});

		return {server: reply.server, devices: reply.devices};
	}

	/** After a server restart: reconnect, reconfigure, resubscribe. */
	async replay()
	{
		const cfg = this.lastConfigure;
		const sub = this.lastSubscribe;

		await this.configure(cfg.devices, cfg.tags);

		if (sub != null)
		{
			await this.subscribe(sub.ids, sub.rateMs, sub.rates);
		}

		this.emit({t: 'server', state: 'ready'});
	}

	/** Closes the socket; the server releases this session's devices. */
	close()
	{
		this.closed = true;
		this.supervisor.off('ready', this.onReady);
		this.lastConfigure = null;
		this.lastSubscribe = null;

		if (this.ws != null)
		{
			try
			{
				this.ws.close();
			}
			catch (e)
			{
				// Already closed.
			}

			this.ws = null;
		}

		this.failPending(new Error('Session closed'));
	}
}

/** Largest request the renderer may make, so a bad page cannot flood the server. */
const LIMITS = {devices: 1000, tags: 50000, addresses: 5000};

function isStringArray(a, max)
{
	return Array.isArray(a) && a.length <= max && a.every((x) => typeof x === 'string');
}

function isPlainObject(o)
{
	return o != null && typeof o === 'object' && !Array.isArray(o);
}

/**
 * Checks the shape of a renderer's hmiComms request; throws on anything
 * malformed. The server validates the content itself.
 */
export function validateCommsArgs(action, args)
{
	const bad = (what) =>
	{
		throw new Error('bad arg: ' + what);
	};

	switch (action)
	{
		case 'hmiComms.configure':
			if (!Array.isArray(args.devices) || args.devices.length > LIMITS.devices ||
				!args.devices.every(isPlainObject))
			{
				bad('devices');
			}

			if (!Array.isArray(args.tags) || args.tags.length > LIMITS.tags ||
				!args.tags.every((t) => isPlainObject(t) && typeof t.id === 'string'))
			{
				bad('tags');
			}

			break;

		case 'hmiComms.subscribe':
			if (!isStringArray(args.ids, LIMITS.tags))
			{
				bad('ids');
			}

			if (typeof args.rateMs !== 'number' || !(args.rateMs >= 10 && args.rateMs <= 3600000))
			{
				bad('rateMs');
			}

			if (args.rates != null && (!isPlainObject(args.rates) ||
				!Object.values(args.rates).every((r) => typeof r === 'number')))
			{
				bad('rates');
			}

			break;

		case 'hmiComms.read':
			if (!isStringArray(args.ids, LIMITS.tags))
			{
				bad('ids');
			}

			break;

		case 'hmiComms.write':
			if (!isPlainObject(args.values) || Object.keys(args.values).length > LIMITS.tags)
			{
				bad('values');
			}

			if (args.timeoutMs != null && typeof args.timeoutMs !== 'number')
			{
				bad('timeoutMs');
			}

			break;

		case 'hmiComms.validate':
			if (typeof args.protocol !== 'string' || !isStringArray(args.addresses, LIMITS.addresses))
			{
				bad('addresses');
			}

			if (args.options != null && !isPlainObject(args.options))
			{
				bad('options');
			}

			break;

		case 'hmiComms.probe':
			if (!isPlainObject(args.device))
			{
				bad('device');
			}

			break;

		case 'hmiComms.unsubscribe':
		case 'hmiComms.status':
		case 'hmiComms.diag':
		case 'hmiComms.disconnect':
			break;

		default:
			bad('action');
	}
}
