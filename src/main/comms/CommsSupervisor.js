// Starts, watches and stops the hmi-comms PLC communications server.
//
// hmi-comms is a separate executable (a .NET single-file build shipped under
// resources/comms). It is spawned lazily -- only when something first needs
// it -- so running plain draw.io never starts it. It is told a random token
// on its stdin, prints one JSON line announcing the port it bound, and exits
// by itself when its stdin closes, so it cannot outlive a crashed parent.

import {spawn as nodeSpawn} from 'child_process';
import crypto from 'crypto';
import fs from 'fs';
import path from 'path';
import {EventEmitter} from 'events';

export const EXE_NAME = (process.platform === 'win32') ? 'hmi-comms.exe' : 'hmi-comms';

// Restart delays after unexpected exits, and the limit on how many restarts
// fit in the window before the supervisor gives up.
const RESTART_DELAYS_MS = [1000, 2000, 5000, 10000, 30000];
const RESTART_LIMIT = 5;
const RESTART_WINDOW_MS = 5 * 60 * 1000;
const START_TIMEOUT_MS = 15000;
const STOP_GRACE_MS = 2000;

/**
 * Where the executable lives.
 *
 * HMI_COMMS_PATH wins, for development against a debug build. Packaged, it
 * is under resources/comms (macOS carries both architectures there). From a
 * source checkout it is wherever `npm run build-comms` published it.
 */
export function resolveExecutable(opts)
{
	const env = opts.env || process.env;
	const platform = opts.platform || process.platform;
	const arch = opts.arch || process.arch;
	const exe = (platform === 'win32') ? 'hmi-comms.exe' : 'hmi-comms';

	if (env.HMI_COMMS_PATH)
	{
		return env.HMI_COMMS_PATH;
	}

	const os = {win32: 'win', darwin: 'mac', linux: 'linux'}[platform];

	if (opts.isPackaged)
	{
		return (platform === 'darwin') ?
			path.join(opts.resourcesPath, 'comms', 'osx-' + arch, exe) :
			path.join(opts.resourcesPath, 'comms', exe);
	}

	return (platform === 'darwin') ?
		path.join(opts.appPath, 'comms', 'publish', 'mac', 'osx-' + arch, exe) :
		path.join(opts.appPath, 'comms', 'publish', os + '-' + arch, exe);
}

/**
 * Reads the first line of a stream, which must be the startup event.
 */
function readFirstLine(stream, timeoutMs, setTimer, clearTimer)
{
	return new Promise((resolve, reject) =>
	{
		let buffer = '';

		const timer = setTimer(() =>
		{
			cleanup();
			reject(new Error('hmi-comms did not report its port within ' + timeoutMs + ' ms'));
		}, timeoutMs);

		function onData(chunk)
		{
			buffer += chunk.toString('utf8');
			const nl = buffer.indexOf('\n');

			if (nl >= 0)
			{
				cleanup();

				try
				{
					resolve(JSON.parse(buffer.substring(0, nl)));
				}
				catch (e)
				{
					reject(new Error('hmi-comms startup line is not JSON: ' + buffer.substring(0, nl)));
				}
			}
		}

		function onEnd()
		{
			cleanup();
			reject(new Error('hmi-comms exited before reporting its port'));
		}

		function cleanup()
		{
			clearTimer(timer);
			stream.off('data', onData);
			stream.off('end', onEnd);
		}

		stream.on('data', onData);
		stream.on('end', onEnd);
	});
}

/**
 * Events:
 *  'ready'   {port, token, pid}  each time a server instance is listening
 *  'exit'    {code, signal, expected}
 *  'failed'  Error               startup failed or restarts exhausted
 *  'log'     line                a line of the server's stderr
 */
export class CommsSupervisor extends EventEmitter
{
	constructor(opts)
	{
		super();
		this.executable = opts.executable;
		this.spawn = opts.spawn || nodeSpawn;
		this.env = opts.env || process.env;
		this.extractDir = opts.extractDir || null;
		this.setTimer = opts.setTimer || setTimeout;
		this.clearTimer = opts.clearTimer || clearTimeout;
		this.now = opts.now || Date.now;
		this.exists = opts.exists || fs.existsSync;
		this.parentPid = opts.parentPid || process.pid;

		this.child = null;
		this.info = null;
		this.starting = null;
		this.stopping = false;
		this.restarts = [];
		this.restartTimer = null;
	}

	get running()
	{
		return this.info != null;
	}

	/**
	 * Resolves to {port, token, pid} once the server is listening, starting it
	 * if needed. Concurrent callers share one start.
	 */
	ensureStarted()
	{
		if (this.info != null)
		{
			return Promise.resolve(this.info);
		}

		if (this.starting == null)
		{
			this.stopping = false;

			const p = this.startOnce();
			this.starting = p;

			// 'ready' is announced only once the start is no longer pending: a
			// listener that reacts to it -- or a server that dies straight away
			// -- must be able to start a fresh instance, not get this one back.
			p.then((info) =>
			{
				if (this.starting === p)
				{
					this.starting = null;
				}

				this.emit('ready', info);
			}, () =>
			{
				if (this.starting === p)
				{
					this.starting = null;
				}
			});
		}

		return this.starting;
	}

	async startOnce()
	{
		if (!this.exists(this.executable))
		{
			throw new Error('hmi-comms is not built: ' + this.executable +
				' (run npm run build-comms)');
		}

		const token = crypto.randomBytes(32).toString('hex');
		const env = Object.assign({}, this.env);

		// A single-file .NET app may need to extract itself; AppImage and
		// snap mounts are read-only, so point it somewhere writable.
		if (this.extractDir != null)
		{
			env.DOTNET_BUNDLE_EXTRACT_BASE_DIR = this.extractDir;
		}

		const child = this.spawn(this.executable, ['--listen', '127.0.0.1:0',
			'--token-stdin', '--allow-shutdown', '--parent-pid', String(this.parentPid)],
			{stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true, env: env});

		this.child = child;

		child.stderr.on('data', (chunk) =>
		{
			for (const line of chunk.toString('utf8').split(/\r?\n/))
			{
				if (line.length > 0)
				{
					this.emit('log', line);
				}
			}
		});

		child.on('error', (e) =>
		{
			this.emit('log', 'spawn error: ' + e.message);
		});

		child.on('exit', (code, signal) =>
		{
			this.onExit(child, code, signal);
		});

		// The token goes on stdin, not the command line, where any local user
		// could read it. Stdin then stays open: its closing is how the server
		// learns this process has gone.
		child.stdin.on('error', () => {});
		child.stdin.write(token + '\n');

		const ev = await readFirstLine(child.stdout, START_TIMEOUT_MS,
			this.setTimer, this.clearTimer);

		if (ev.event !== 'listening')
		{
			throw new Error('hmi-comms failed to start: ' + (ev.message || JSON.stringify(ev)));
		}

		this.info = {port: ev.port, token: token, pid: ev.pid, version: ev.version};

		return this.info;
	}

	onExit(child, code, signal)
	{
		if (child !== this.child)
		{
			return;
		}

		const expected = this.stopping;
		this.child = null;
		this.info = null;
		this.emit('exit', {code: code, signal: signal, expected: expected});

		if (!expected)
		{
			this.scheduleRestart();
		}
	}

	scheduleRestart()
	{
		const now = this.now();
		this.restarts = this.restarts.filter((t) => now - t < RESTART_WINDOW_MS);

		if (this.restarts.length >= RESTART_LIMIT)
		{
			this.emit('failed', new Error('hmi-comms keeps exiting; gave up after ' +
				RESTART_LIMIT + ' restarts'));

			return;
		}

		const delay = RESTART_DELAYS_MS[Math.min(this.restarts.length,
			RESTART_DELAYS_MS.length - 1)];
		this.restarts.push(now);

		this.restartTimer = this.setTimer(() =>
		{
			this.restartTimer = null;

			if (!this.stopping)
			{
				this.ensureStarted().catch((e) =>
				{
					this.emit('failed', e);
				});
			}
		}, delay);
	}

	/**
	 * Stops the server: closes its stdin (it exits on end-of-file), then kills
	 * it if it is still there after a grace period.
	 */
	stop()
	{
		this.stopping = true;

		if (this.restartTimer != null)
		{
			this.clearTimer(this.restartTimer);
			this.restartTimer = null;
		}

		const child = this.child;

		if (child == null)
		{
			return Promise.resolve();
		}

		return new Promise((resolve) =>
		{
			const timer = this.setTimer(() =>
			{
				try
				{
					child.kill();
				}
				catch (e)
				{
					// Already gone.
				}

				resolve();
			}, STOP_GRACE_MS);

			child.once('exit', () =>
			{
				this.clearTimer(timer);
				resolve();
			});

			try
			{
				child.stdin.end();
			}
			catch (e)
			{
				child.kill();
			}
		});
	}
}
