// The command-line API (doc/HMI_AUTOMATION.md): build, check, dump, render.
// Runs the real app, so it needs a display (skipped without one, e.g. in CI
// without xvfb). Publishing is covered by publish.test.js.
import { test, describe, after } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'child_process';
import { createRequire } from 'module';
import fs from 'fs';
import os from 'os';
import path from 'path';
import { fileURLToPath } from 'url';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const electron = (() => { try { return createRequire(import.meta.url)('electron'); } catch (e) { return null; } })();
const skip = (electron == null || !process.env.DISPLAY) && 'needs electron and a display';
const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'hmi-cli-test-'));

after(() => fs.rmSync(dir, {recursive: true, force: true}));

function studio(...args)
{
	const r = spawnSync(electron, [root, '--no-sandbox', ...args], {encoding: 'utf8', timeout: 120000});

	return {status: r.status, out: r.stdout, err: r.stderr};
}

const spec = {
	version: 1,
	settings: {width: 800, height: 480, startup: ['Main'], runtime: {windowMode: 'window', exit: 'shortcut'}},
	devices: [{name: 'Sim', protocol: 'simulator'}],
	tags: [
		{name: 'Level', type: 'MemoryReal', comment: 'Tank level', initial: 50, maxEU: 100, alarms: {high: 90}},
		{name: 'Pump', type: 'MemoryDiscrete', comment: 'Pump', alarms: {state: 'on'}}
	],
	pages: [
		{name: 'Main', background: '#ffffff', objects: [
			{id: 'tank', type: 'cylinder', x: 40, y: 40, width: 100, height: 160, label: 'Tank',
				links: {'percentFill.vertical': {expr: 'Level'}}},
			{id: 'go', type: 'button', x: 200, y: 40, width: 120, height: 40, label: 'Alarms',
				links: {showWindow: {window: 'Alarms'}}},
			{edge: true, source: 'tank', target: 'go'}
		]},
		{name: 'Alarms', window: {type: 'popup', titleBar: true, x: 100, y: 100, width: 600, height: 300},
			objects: [{id: 'list', type: 'alarmList', x: 100, y: 124, width: 600, height: 276}]}
	]
};

describe('hmi command line', {skip}, () =>
{
	const specFile = path.join(dir, 'plant.json');
	const project = path.join(dir, 'plant.ahmi');
	fs.writeFileSync(specFile, JSON.stringify(spec));

	test('build writes a checked project', () =>
	{
		const r = studio('--hmi-build', specFile, '-o', project);

		assert.equal(r.status, 0, r.out + r.err);
		assert.match(r.out, /OK: no problems found/);
		const xml = fs.readFileSync(project, 'utf8');
		assert.match(xml, /hmiVersion="1"/);
		assert.equal((xml.match(/<diagram /g) || []).length, 2);
	});

	test('check passes, and fails with problems on a broken project', () =>
	{
		assert.equal(studio('--hmi-check', project).status, 0);

		const broken = JSON.parse(JSON.stringify(spec));
		broken.pages[0].objects[0].links['percentFill.vertical'].expr = 'Levl + 1';
		const bf = path.join(dir, 'broken.json');
		fs.writeFileSync(bf, JSON.stringify(broken));
		const r = studio('--hmi-build', bf, '-o', path.join(dir, 'broken.ahmi'));

		assert.equal(r.status, 2, r.out + r.err);
		assert.match(r.out, /PROBLEM Main \| cell tank "Tank" \| .*unknown tag "Levl"/);
	});

	test('spec errors write nothing', () =>
	{
		const bad = JSON.parse(JSON.stringify(spec));
		bad.tags[0].colour = 'red';
		const bf = path.join(dir, 'bad.json');
		fs.writeFileSync(bf, JSON.stringify(bad));
		const r = studio('--hmi-build', bf, '-o', path.join(dir, 'bad.ahmi'));

		assert.equal(r.status, 1);
		assert.match(r.err, /unknown field "colour"/);
		assert.ok(!fs.existsSync(path.join(dir, 'bad.ahmi')));
	});

	test('dump and build back is lossless', () =>
	{
		const d1 = path.join(dir, 'd1.json');
		const d2 = path.join(dir, 'd2.json');
		const again = path.join(dir, 'again.ahmi');

		assert.equal(studio('--hmi-dump', project, '-o', d1).status, 0);
		assert.equal(studio('--hmi-build', d1, '-o', again).status, 0);
		assert.equal(studio('--hmi-dump', again, '-o', d2).status, 0);
		assert.deepEqual(JSON.parse(fs.readFileSync(d2, 'utf8')), JSON.parse(fs.readFileSync(d1, 'utf8')));
	});

	test('render writes a PNG per page', () =>
	{
		const out = path.join(dir, 'pages');
		const r = studio('--hmi-render', project, '-o', out);

		assert.equal(r.status, 0, r.out + r.err);
		assert.deepEqual(fs.readdirSync(out).sort(), ['01-Main.png', '02-Alarms.png']);
		assert.equal(fs.readFileSync(path.join(out, '01-Main.png')).subarray(1, 4).toString(), 'PNG');
	});
});
