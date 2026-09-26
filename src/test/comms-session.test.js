// Unit tests for src/main/comms/CommsSession.js -- the main process's relay
// between a renderer and hmi-comms -- and its argument checks.
import { test, describe } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'fs';
import path from 'path';
import { EventEmitter } from 'events';
import { fileURLToPath } from 'url';
import { CommsSession, validateCommsArgs } from '../main/comms/CommsSession.js';
import { CommsSupervisor, resolveExecutable } from '../main/comms/CommsSupervisor.js';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', '..');

/**
 * A server stand-in behind a fake WebSocket: answers hello, configure (handles
 * 1..n), subscribe (reply then snapshot), write, and lets the test push.
 */
function fakeServer()
{
	const server = {sockets: [], received: [], handles: {}};

	class FakeSocket
	{
		constructor(url)
		{
			this.url = url;
			this.readyState = 0;
			server.sockets.push(this);
			setImmediate(() =>
			{
				this.readyState = 1;
				this.onopen && this.onopen();
			});
		}

		send(text)
		{
			const m = JSON.parse(text);
			server.received.push(m);
			const reply = (body) => setImmediate(() => this.onmessage({data: JSON.stringify(
				Object.assign({id: m.id}, body))}));

			switch (m.t)
			{
				case 'hello':
					reply({t: 'hello', ok: m.token === 'tok'});
					break;
				case 'configure':
					server.handles = {};
					reply({t: 'configureResult', ok: true, errors: [], tags: m.tags.map((t, i) =>
					{
						server.handles[t.id] = i + 1;
						return {id: t.id, h: i + 1, normalized: t.address.toUpperCase()};
					})});
					break;
				case 'subscribe':
					reply({t: 'subscribeResult', ok: true});
					setImmediate(() => this.push({t: 'snapshot', values: m.tags.map((id) =>
						[server.handles[id], 0, 0, 1, 'waiting'])}));
					break;
				case 'write':
					reply({t: 'writeResult', results: Object.fromEntries(Object.keys(m.values)
						.map((k) => [k, {ok: true}]))});
					break;
				case 'fail':
					reply({t: 'error', code: 'bad_request', message: 'nope'});
					break;
				default:
					reply({t: m.t + 'Result', ok: true});
			}
		}

		push(msg)
		{
			this.onmessage({data: JSON.stringify(msg)});
		}

		close()
		{
			this.readyState = 3;
			this.onclose && this.onclose();
		}
	}

	server.WebSocket = FakeSocket;

	return server;
}

function fakeSupervisor(pid)
{
	const sup = new EventEmitter();
	sup.pid = pid || 100;
	sup.starts = 0;
	sup.ensureStarted = async () =>
	{
		sup.starts++;
		const info = {port: 5000, token: 'tok', pid: sup.pid};
		sup.emit('ready', info);
		return info;
	};

	return sup;
}

const tick = () => new Promise((r) => setImmediate(r));

describe('CommsSession', () =>
{
	test('configure maps handles back to ids', async () =>
	{
		const server = fakeServer();
		const events = [];
		const s = new CommsSession(fakeSupervisor(), (e) => events.push(e), {WebSocket: server.WebSocket});

		const r = await s.configure([{name: 'P', protocol: 'modbus'}],
			[{id: 'Level', device: 'P', address: 'hr:0'}, {id: 'Flow', device: 'P', address: 'hr:2'}]);

		assert.deepEqual(r.tags.map((t) => t.normalized), ['HR:0', 'HR:2']);
		assert.equal(server.received[0].t, 'hello');
		assert.equal(server.received[0].token, 'tok');

		server.sockets[0].push({t: 'change', values: [[2, 12.5, 192, 1000], [9, 1, 192, 1]]});
		assert.deepEqual(events.at(-1), {t: 'change', values: {Flow: [12.5, 192, 1000]}});
	});

	test('the snapshot for a subscription arrives as an event', async () =>
	{
		const server = fakeServer();
		const events = [];
		const s = new CommsSession(fakeSupervisor(), (e) => events.push(e), {WebSocket: server.WebSocket});
		await s.configure([], [{id: 'A', device: 'P', address: 'x'}]);

		const r = await s.subscribe(['A'], 250);
		await tick();

		assert.equal(r.ok, true);
		assert.deepEqual(events.at(-1), {t: 'snapshot', values: {A: [0, 0, 1, 'waiting']}});
	});

	test('status pushes pass through', async () =>
	{
		const server = fakeServer();
		const events = [];
		const s = new CommsSession(fakeSupervisor(), (e) => events.push(e), {WebSocket: server.WebSocket});
		await s.configure([], []);

		server.sockets[0].push({t: 'status', devices: [{name: 'P', state: 'backoff'}]});

		assert.deepEqual(events.at(-1), {t: 'status', devices: [{name: 'P', state: 'backoff'}]});
	});

	test('error replies reject', async () =>
	{
		const server = fakeServer();
		const s = new CommsSession(fakeSupervisor(), () => {}, {WebSocket: server.WebSocket});

		await assert.rejects(s.request({t: 'fail'}), /nope/);
	});

	test('concurrent requests share one connection', async () =>
	{
		const server = fakeServer();
		const sup = fakeSupervisor();
		const s = new CommsSession(sup, () => {}, {WebSocket: server.WebSocket});

		await Promise.all([s.status(), s.diag(), s.write({A: 1})]);

		assert.equal(server.sockets.length, 1);
		assert.equal(server.received.filter((m) => m.t === 'hello').length, 1);
	});

	test('a restarted server gets the last configure and subscribe again', async () =>
	{
		const server = fakeServer();
		const sup = fakeSupervisor(100);
		const events = [];
		const s = new CommsSession(sup, (e) => events.push(e), {WebSocket: server.WebSocket});

		await s.configure([{name: 'P'}], [{id: 'A', device: 'P', address: 'x'}]);
		await s.subscribe(['A'], 500);

		// The first start announced itself too; it must not have replayed.
		assert.equal(server.received.filter((m) => m.t === 'configure').length, 1);

		server.sockets[0].close();
		assert.equal(events.at(-1).state, 'disconnected');

		sup.pid = 200;
		sup.emit('ready', {port: 5000, token: 'tok', pid: 200});

		for (let i = 0; i < 20 && !events.some((e) => e.state === 'ready'); i++)
		{
			await tick();
		}

		assert.equal(server.sockets.length, 2);
		const after = server.received.slice(server.received.findIndex((m, i) => i > 0 && m.t === 'hello'));
		assert.deepEqual(after.map((m) => m.t), ['hello', 'configure', 'subscribe']);
		assert.equal(after[2].rateMs, 500);
		assert.ok(events.some((e) => e.t === 'server' && e.state === 'ready'));
	});

	test('close stops listening and rejects anything pending', async () =>
	{
		const server = fakeServer();
		const sup = fakeSupervisor();
		const s = new CommsSession(sup, () => {}, {WebSocket: server.WebSocket});
		await s.configure([], []);

		s.close();

		assert.equal(sup.listenerCount('ready'), 0);
		assert.equal(server.sockets[0].readyState, 3);
		await assert.rejects(s.send({t: 'status'}), /Not connected/);
	});
});

describe('validateCommsArgs', () =>
{
	test('accepts well-formed requests', () =>
	{
		validateCommsArgs('hmiComms.configure', {devices: [{name: 'P'}], tags: [{id: 'A'}]});
		validateCommsArgs('hmiComms.subscribe', {ids: ['A'], rateMs: 250});
		validateCommsArgs('hmiComms.write', {values: {A: 1}});
		validateCommsArgs('hmiComms.validate', {protocol: 'modbus', addresses: ['HR:0']});
		validateCommsArgs('hmiComms.probe', {device: {name: 'P'}});
		validateCommsArgs('hmiComms.status', {});
	});

	test('refuses malformed ones', () =>
	{
		const cases = [
			['hmiComms.configure', {devices: 'x', tags: []}],
			['hmiComms.configure', {devices: [], tags: [{address: 'no id'}]}],
			['hmiComms.subscribe', {ids: [1, 2], rateMs: 250}],
			['hmiComms.subscribe', {ids: ['A'], rateMs: 1}],
			['hmiComms.write', {values: []}],
			['hmiComms.validate', {protocol: 'modbus', addresses: 'HR:0'}],
			['hmiComms.probe', {}],
			['hmiComms.shell', {}]
		];

		for (const [action, args] of cases)
		{
			assert.throws(() => validateCommsArgs(action, args), /bad arg/, action + ' ' + JSON.stringify(args));
		}
	});

	test('caps request sizes', () =>
	{
		const many = Array.from({length: 50001}, (_, i) => 'T' + i);

		assert.throws(() => validateCommsArgs('hmiComms.subscribe', {ids: many, rateMs: 250}), /bad arg/);
	});
});

const built = resolveExecutable({env: {}, platform: process.platform, arch: process.arch,
	isPackaged: false, appPath: root});

describe('real hmi-comms session', {skip: !fs.existsSync(built) && 'not built (npm run build-comms)'}, () =>
{
	test('configure, validate and a device that cannot be reached', async () =>
	{
		const sup = new CommsSupervisor({executable: built});
		const events = [];
		const s = new CommsSession(sup, (e) => events.push(e));

		try
		{
			const v = await s.validate('slc', ['N7:0', 'B3/37', 'Q1:0']);
			assert.equal(v.results[1].normalized, 'B3:2/5');
			assert.equal(v.results[2].ok, false);

			// Port 1 on loopback refuses at once.
			const cfg = await s.configure([{name: 'P', protocol: 'modbus', host: '127.0.0.1', port: 1}],
				[{id: 'Level', device: 'P', address: 'HR:0'}]);
			assert.equal(cfg.tags[0].normalized, 'HR:0:INT16:BE');

			await s.subscribe(['Level'], 100);

			for (let i = 0; i < 100 && !events.some((e) => e.t === 'change' && e.values.Level?.[3] === 'comm'); i++)
			{
				await new Promise((r) => setTimeout(r, 50));
			}

			const snapshot = events.find((e) => e.t === 'snapshot');
			assert.deepEqual(snapshot.values.Level.slice(0, 2), [null, 0]);
			assert.ok(events.some((e) => e.t === 'change' && e.values.Level[3] === 'comm'), JSON.stringify(events));
			assert.ok(events.some((e) => e.t === 'status' && e.devices[0].name === 'P'));

			const probe = await s.probe({name: 'P', protocol: 'modbus', host: '127.0.0.1', port: 1});
			assert.equal(probe.ok, false);
		}
		finally
		{
			s.close();
			await sup.stop();
		}
	});
});
