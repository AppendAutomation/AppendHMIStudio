// Run-only mode for published HMI packages — exercises src/main/runtime/RuntimeMode.js
import { test, describe, after } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'fs';
import os from 'os';
import path from 'path';
import { findRuntimeDir, loadRuntimeConfig, runtimeUserDataDir, runtimeWindowOptions, mayExit,
	hashExitPassword, readRuntimeProject, publicRuntimeInfo } from '../main/runtime/RuntimeMode.js';

const tempDirs = [];

function tempDir()
{
	const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'hmi-runtime-test-'));
	tempDirs.push(dir);

	return dir;
}

after(() =>
{
	for (const dir of tempDirs)
	{
		fs.rmSync(dir, {recursive: true, force: true});
	}
});

function writeRuntime(dir, config)
{
	fs.writeFileSync(path.join(dir, 'runtime.json'), JSON.stringify(config));
}

describe('findRuntimeDir', () =>
{
	test('the editor is not a runtime', () =>
	{
		assert.equal(findRuntimeDir({argv: ['electron', '.'], defaultApp: true, isPackaged: false}), null);
		assert.equal(findRuntimeDir({argv: ['draw.io.exe', 'a.drawio'], defaultApp: false,
			isPackaged: true, resourcesPath: tempDir()}), null);
	});

	test('--hmi-runtime names the folder, in dev and packaged argv layouts', () =>
	{
		assert.equal(findRuntimeDir({argv: ['electron', '.', '--hmi-runtime', '/x/rt'], defaultApp: true}),
			path.resolve('/x/rt'));
		assert.equal(findRuntimeDir({argv: ['app.exe', '--hmi-runtime=/x/rt'], defaultApp: false}),
			path.resolve('/x/rt'));
	});

	test('a packaged app with resources/hmi-runtime/runtime.json is a runtime', () =>
	{
		const res = tempDir();
		fs.mkdirSync(path.join(res, 'hmi-runtime'));
		writeRuntime(path.join(res, 'hmi-runtime'), {});

		assert.equal(findRuntimeDir({argv: ['app.exe'], defaultApp: false, isPackaged: true, resourcesPath: res}),
			path.join(res, 'hmi-runtime'));
		// Only when packaged
		assert.equal(findRuntimeDir({argv: ['electron', '.'], defaultApp: true, isPackaged: false, resourcesPath: res}),
			null);
	});
});

describe('loadRuntimeConfig', () =>
{
	test('defaults', () =>
	{
		const dir = tempDir();
		writeRuntime(dir, {productName: 'Line 3'});
		const c = loadRuntimeConfig(dir);

		assert.equal(c.productName, 'Line 3');
		assert.equal(c.windowMode, 'kiosk');
		assert.equal(c.exit.mode, 'shortcut');
		assert.equal(c.projectPath, path.join(dir, 'project.ahmi'));
		assert.equal(c.width, 1024);
		assert.equal(c.height, 768);
	});

	test('unknown modes and odd sizes fall back', () =>
	{
		const dir = tempDir();
		writeRuntime(dir, {windowMode: 'maximised', exit: {mode: 'sometimes'}, width: 12, height: '1080'});
		const c = loadRuntimeConfig(dir);

		assert.equal(c.windowMode, 'kiosk');
		assert.equal(c.exit.mode, 'shortcut');
		assert.equal(c.width, 1024);
		assert.equal(c.height, 1080);
	});

	test('the project must be in the runtime folder', () =>
	{
		const dir = tempDir();
		writeRuntime(dir, {project: '../elsewhere.drawio'});
		assert.throws(() => loadRuntimeConfig(dir), /Invalid project/);
	});

	test('password mode needs a hash', () =>
	{
		const dir = tempDir();
		writeRuntime(dir, {exit: {mode: 'password', salt: 'ab'}});
		assert.throws(() => loadRuntimeConfig(dir), /password/);
	});

	test('a missing or broken runtime.json gives a readable reason', () =>
	{
		const dir = tempDir();
		assert.throws(() => loadRuntimeConfig(dir), /Cannot load/);
		fs.writeFileSync(path.join(dir, 'runtime.json'), '{nope');
		assert.throws(() => loadRuntimeConfig(dir), /Cannot load/);
	});

	test('product names are cleaned for use as a folder name', () =>
	{
		const dir = tempDir();
		writeRuntime(dir, {productName: 'A/B:C*'});
		assert.equal(loadRuntimeConfig(dir).productName, 'ABC');
	});
});

describe('runtimeUserDataDir', () =>
{
	test('its own folder, never the editor\'s', () =>
	{
		assert.equal(runtimeUserDataDir('/appdata', {productName: 'Line 3'}), path.join('/appdata', 'Line 3'));
		assert.equal(runtimeUserDataDir('/appdata', {productName: 'Append HMI Studio'}),
			path.join('/appdata', 'Append HMI Studio Runtime'));
		assert.equal(runtimeUserDataDir('/appdata', {productName: 'append hmi studio'}),
			path.join('/appdata', 'append hmi studio Runtime'));
	});
});

describe('runtimeWindowOptions', () =>
{
	const base = {productName: 'P', width: 1280, height: 800};

	test('kiosk', () =>
	{
		const o = runtimeWindowOptions(Object.assign({windowMode: 'kiosk'}, base));
		assert.equal(o.kiosk, true);
		assert.equal(o.frame, false);
	});

	test('fullscreen', () =>
	{
		const o = runtimeWindowOptions(Object.assign({windowMode: 'fullscreen'}, base));
		assert.equal(o.fullscreen, true);
		assert.equal(o.kiosk, undefined);
	});

	test('window is the project resolution, fixed', () =>
	{
		const o = runtimeWindowOptions(Object.assign({windowMode: 'window'}, base));
		assert.deepEqual([o.width, o.height, o.useContentSize, o.resizable], [1280, 800, true, false]);
	});
});

describe('exit policy', () =>
{
	const salt = 'c0ffee';
	const password = {exit: {mode: 'password', salt: salt, hash: hashExitPassword(salt, 'open sesame')}};

	test('shortcut always exits, never never does', () =>
	{
		assert.equal(mayExit({exit: {mode: 'shortcut'}}), true);
		assert.equal(mayExit({exit: {mode: 'never'}}, 'anything'), false);
	});

	test('password mode checks the salted hash', () =>
	{
		assert.equal(mayExit(password, 'open sesame'), true);
		assert.equal(mayExit(password, 'open sesame '), false);
		assert.equal(mayExit(password, ''), false);
		assert.equal(mayExit(password), false);
	});

	test('the renderer is never told the hash', () =>
	{
		const dir = tempDir();
		writeRuntime(dir, {productName: 'P', exit: password.exit});
		const info = publicRuntimeInfo(loadRuntimeConfig(dir));

		assert.equal(info.exitMode, 'password');
		assert.ok(!JSON.stringify(info).includes(password.exit.hash));
		assert.ok(!JSON.stringify(info).includes(salt));
	});
});

describe('readRuntimeProject', () =>
{
	test('reads the project file', async () =>
	{
		const dir = tempDir();
		writeRuntime(dir, {});
		fs.writeFileSync(path.join(dir, 'project.ahmi'), '<mxfile/>');
		assert.equal(await readRuntimeProject(loadRuntimeConfig(dir)), '<mxfile/>');
	});

	test('refuses a directory', async () =>
	{
		const dir = tempDir();
		writeRuntime(dir, {});
		fs.mkdirSync(path.join(dir, 'project.ahmi'));
		await assert.rejects(readRuntimeProject(loadRuntimeConfig(dir)), /Not a usable/);
	});
});
