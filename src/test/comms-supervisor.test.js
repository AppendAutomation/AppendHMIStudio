// Unit tests for src/main/comms/CommsSupervisor.js -- how the hmi-comms PLC
// server is located, started, restarted and stopped -- plus a check that the
// electron-builder configs ship it outside the asar.
import { test, describe } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'fs';
import path from 'path';
import { EventEmitter } from 'events';
import { PassThrough } from 'stream';
import { fileURLToPath } from 'url';
import { CommsSupervisor, resolveExecutable, EXE_NAME } from '../main/comms/CommsSupervisor.js';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const readConfig = (name) => JSON.parse(fs.readFileSync(path.join(root, name), 'utf8'));

/** A child process stand-in whose stdout, stdin and exit the test drives. */
function fakeChild()
{
	const child = new EventEmitter();
	child.stdin = new PassThrough();
	child.stdout = new PassThrough();
	child.stderr = new PassThrough();
	child.written = '';
	child.stdin.on('data', (d) => { child.written += d.toString(); });
	child.killed = false;
	child.kill = () => { child.killed = true; child.emit('exit', null, 'SIGTERM'); };

	return child;
}

/** Timers the test fires by hand. */
function fakeTimers()
{
	const pending = [];

	return {
		pending: pending,
		setTimer: (fn, ms) => { const t = {fn: fn, ms: ms}; pending.push(t); return t; },
		clearTimer: (t) => { const i = pending.indexOf(t); if (i >= 0) pending.splice(i, 1); },
		fire: (ms) =>
		{
			const t = pending.find((p) => p.ms === ms);
			assert.ok(t, 'no timer of ' + ms + ' ms pending (have ' +
				pending.map((p) => p.ms).join(',') + ')');
			pending.splice(pending.indexOf(t), 1);
			t.fn();
		}
	};
}

function makeSupervisor(extra)
{
	const children = [];
	const timers = fakeTimers();
	let clock = 0;

	const sup = new CommsSupervisor(Object.assign({
		executable: '/opt/hmi-comms',
		exists: () => true,
		spawn: (exe, args, opts) =>
		{
			const c = fakeChild();
			c.exe = exe;
			c.args = args;
			c.opts = opts;
			children.push(c);

			return c;
		},
		setTimer: timers.setTimer,
		clearTimer: timers.clearTimer,
		now: () => clock,
		parentPid: 4242
	}, extra || {}));

	return {sup: sup, children: children, timers: timers,
		advance: (ms) => { clock += ms; }};
}

function announce(child, port)
{
	child.stdout.write(JSON.stringify({event: 'listening', port: port, pid: 99,
		version: '0.1.0'}) + '\n');
}

describe('resolveExecutable', () =>
{
	test('environment override wins', () =>
	{
		assert.equal(resolveExecutable({env: {HMI_COMMS_PATH: '/x/y'}, platform: 'linux',
			arch: 'x64', isPackaged: true, resourcesPath: '/r'}), '/x/y');
	});

	test('packaged windows and linux use resources/comms', () =>
	{
		assert.equal(resolveExecutable({env: {}, platform: 'win32', arch: 'x64',
			isPackaged: true, resourcesPath: 'C:\\app\\resources'}),
			path.join('C:\\app\\resources', 'comms', 'hmi-comms.exe'));
		assert.equal(resolveExecutable({env: {}, platform: 'linux', arch: 'arm64',
			isPackaged: true, resourcesPath: '/opt/drawio/resources'}),
			path.join('/opt/drawio/resources', 'comms', 'hmi-comms'));
	});

	test('packaged mac picks its architecture', () =>
	{
		assert.equal(resolveExecutable({env: {}, platform: 'darwin', arch: 'arm64',
			isPackaged: true, resourcesPath: '/A/Resources'}),
			path.join('/A/Resources', 'comms', 'osx-arm64', 'hmi-comms'));
	});

	test('source checkout uses the publish folder', () =>
	{
		assert.equal(resolveExecutable({env: {}, platform: 'linux', arch: 'x64',
			isPackaged: false, appPath: '/src'}),
			path.join('/src', 'comms', 'publish', 'linux-x64', 'hmi-comms'));
		assert.equal(resolveExecutable({env: {}, platform: 'darwin', arch: 'x64',
			isPackaged: false, appPath: '/src'}),
			path.join('/src', 'comms', 'publish', 'mac', 'osx-x64', 'hmi-comms'));
	});
});

describe('CommsSupervisor', () =>
{
	test('starts with the token on stdin, not the command line', async () =>
	{
		const {sup, children} = makeSupervisor();
		const started = sup.ensureStarted();

		assert.equal(children.length, 1);
		const c = children[0];
		announce(c, 51000);
		const info = await started;

		assert.equal(info.port, 51000);
		assert.match(info.token, /^[0-9a-f]{64}$/);
		assert.equal(c.written, info.token + '\n');
		assert.ok(!c.args.join(' ').includes(info.token), 'token leaked into argv');
		assert.deepEqual(c.args.slice(0, 4), ['--listen', '127.0.0.1:0', '--token-stdin',
			'--allow-shutdown']);
		assert.equal(c.args[c.args.indexOf('--parent-pid') + 1], '4242');
		assert.equal(c.opts.windowsHide, true);
		assert.ok(sup.running);
	});

	test('concurrent callers share one start', async () =>
	{
		const {sup, children} = makeSupervisor();
		const a = sup.ensureStarted();
		const b = sup.ensureStarted();

		assert.equal(children.length, 1);
		announce(children[0], 1);
		assert.equal(await a, await b);
		assert.equal(await sup.ensureStarted(), await a);
		assert.equal(children.length, 1);
	});

	test('a fatal startup line rejects', async () =>
	{
		const {sup, children} = makeSupervisor();
		const started = sup.ensureStarted();
		children[0].stdout.write('{"event":"fatal","message":"no token"}\n');

		await assert.rejects(started, /no token/);
		assert.ok(!sup.running);
	});

	test('a missing executable says how to build it', async () =>
	{
		const {sup} = makeSupervisor({exists: () => false});

		await assert.rejects(sup.ensureStarted(), /npm run build-comms/);
	});

	test('the bundle extraction directory is passed in the environment', async () =>
	{
		const {sup, children} = makeSupervisor({extractDir: '/home/u/.config/app/comms-cache',
			env: {PATH: '/bin'}});
		const started = sup.ensureStarted();
		announce(children[0], 2);
		await started;

		assert.equal(children[0].opts.env.DOTNET_BUNDLE_EXTRACT_BASE_DIR,
			'/home/u/.config/app/comms-cache');
		assert.equal(children[0].opts.env.PATH, '/bin');
	});

	test('an unexpected exit restarts after a backoff', async () =>
	{
		const {sup, children, timers} = makeSupervisor();
		const exits = [];
		sup.on('exit', (e) => exits.push(e));

		const started = sup.ensureStarted();
		announce(children[0], 10);
		await started;

		children[0].emit('exit', 1, null);
		assert.equal(exits[0].expected, false);
		assert.ok(!sup.running);

		const ready = new Promise((resolve) => sup.once('ready', resolve));
		timers.fire(1000);
		assert.equal(children.length, 2);
		announce(children[1], 11);
		const info = await ready;

		assert.equal(info.port, 11);
		assert.notEqual(children[1].written, children[0].written, 'token reused');
	});

	test('restarts back off and then give up', async () =>
	{
		const {sup, children, timers} = makeSupervisor();
		let failed = null;
		sup.on('failed', (e) => { failed = e; });

		let started = sup.ensureStarted();
		announce(children[0], 1);
		await started;

		const delays = [1000, 2000, 5000, 10000, 30000];

		for (let i = 0; i < delays.length; i++)
		{
			const ready = new Promise((resolve) => sup.once('ready', resolve));
			children[children.length - 1].emit('exit', 1, null);
			timers.fire(delays[i]);
			announce(children[children.length - 1], 100 + i);
			await ready;
		}

		children[children.length - 1].emit('exit', 1, null);

		assert.ok(failed, 'did not give up');
		assert.match(failed.message, /gave up/);
		assert.equal(timers.pending.length, 0);
	});

	test('restart count resets after the window', async () =>
	{
		const {sup, children, timers, advance} = makeSupervisor();
		const started = sup.ensureStarted();
		announce(children[0], 1);
		await started;

		for (let i = 0; i < 3; i++)
		{
			const ready = new Promise((resolve) => sup.once('ready', resolve));
			children[children.length - 1].emit('exit', 1, null);
			timers.fire(timers.pending[0].ms);
			announce(children[children.length - 1], 1);
			await ready;
		}

		advance(6 * 60 * 1000);
		children[children.length - 1].emit('exit', 1, null);

		// Back to the first delay.
		assert.equal(timers.pending[0].ms, 1000);
	});

	test('stop closes stdin and does not restart', async () =>
	{
		const {sup, children, timers} = makeSupervisor();
		const started = sup.ensureStarted();
		announce(children[0], 1);
		await started;

		const c = children[0];
		let ended = false;
		c.stdin.on('finish', () =>
		{
			ended = true;
			c.emit('exit', 0, null);
		});

		await sup.stop();

		assert.ok(ended, 'stdin was not closed');
		assert.ok(!c.killed);
		assert.equal(timers.pending.length, 0);
		assert.ok(!sup.running);
	});

	test('stop kills a server that ignores stdin', async () =>
	{
		const {sup, children, timers} = makeSupervisor();
		const started = sup.ensureStarted();
		announce(children[0], 1);
		await started;

		const stopped = sup.stop();
		timers.fire(2000);
		await stopped;

		assert.ok(children[0].killed);
	});
});

describe('packaging', () =>
{
	const configs = ['electron-builder-win.json', 'electron-builder-linux.json'];

	test('every config keeps comms out of the asar', () =>
	{
		for (const name of configs)
		{
			assert.ok(readConfig(name).files.includes('!comms{,/**}'), name);
		}
	});

	test('every platform ships the server as an extra resource', () =>
	{
		const from = (cfg) => (cfg.extraResources || []).filter((r) => r.to == 'comms').map((r) => r.from);

		assert.deepEqual(from(readConfig('electron-builder-win.json')), ['comms/publish/win-${arch}']);
		assert.deepEqual(from(readConfig('electron-builder-linux.json')), ['comms/publish/linux-${arch}']);
	});

	test('windows signs the server executable', () =>
	{
		assert.ok(readConfig('electron-builder-win.json').win.signExts.includes('.exe'));
	});
});

// The real thing, when it has been built for this machine.
const built = resolveExecutable({env: {}, platform: process.platform, arch: process.arch,
	isPackaged: false, appPath: root});

describe('real hmi-comms', {skip: !fs.existsSync(built) && 'not built (npm run build-comms)'}, () =>
{
	test('starts, answers ping and stops', async () =>
	{
		const sup = new CommsSupervisor({executable: built});
		const info = await sup.ensureStarted();

		try
		{
			const ws = new WebSocket('ws://127.0.0.1:' + info.port + '/v1');
			const replies = [];

			await new Promise((resolve, reject) =>
			{
				ws.onerror = () => reject(new Error('websocket error'));
				ws.onopen = () => ws.send(JSON.stringify({t: 'hello', id: 1, v: 1,
					token: info.token, client: 'node-test'}));
				ws.onmessage = (ev) =>
				{
					const m = JSON.parse(ev.data);
					replies.push(m);

					if (m.t === 'hello')
					{
						ws.send(JSON.stringify({t: 'ping', id: 2}));
					}
					else if (m.t === 'pong')
					{
						resolve();
					}
				};
			});

			ws.close();
			assert.equal(replies[0].ok, true);
			assert.equal(replies[1].id, 2);
		}
		finally
		{
			await sup.stop();
		}

		assert.ok(!sup.running);
	});

	test('exe name matches the platform', () =>
	{
		assert.equal(path.basename(built), EXE_NAME);
	});
});
