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
	quickFunctions: [
		{name: 'Halved', args: [{name: 'Raw', type: 'Analog'}], body: 'Return Raw / 2;'}
	],
	dataSources: [
		{name: 'Plant', provider: 'mssql', host: 'db.example', port: 1433, database: 'Batches',
			user: 'hmi', password: 'secret', options: {legacyTls: true}},
		{name: 'Local', provider: 'sqlite', file: 'batches.db', folder: 'documents'}
	],
	bindLists: [
		{name: 'BatchRow', items: [{tag: 'Level', column: 'Level'},
			{tag: 'Level', field: 'InAlarm', column: 'InAlarm'}, 'Pump']}
	],
	tableTemplates: [
		{name: 'BatchLog', columns: [{name: 'Level', type: 'number'},
			{name: 'Stamp', type: 'datetime'},
			{name: 'Note', type: 'text', length: 80, allowNull: false, index: 'nonunique'}]}
	],
	queries: [{name: 'Recent', sql: 'SELECT Level, Stamp\nFROM BatchLog\nORDER BY Stamp DESC'}],
	pages: [
		{name: 'Main', background: '#ffffff', objects: [
			{id: 'tank', type: 'cylinder', x: 40, y: 40, width: 100, height: 160, label: 'Tank',
				links: {'percentFill.vertical': {expr: 'Halved(Level)'}}},
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

	// A QuickFunction may not take the name of a built-in, whichever way the
	// project was made.
	test('build refuses a QuickFunction named after a built-in', () =>
	{
		const broken = JSON.parse(JSON.stringify(spec));
		broken.quickFunctions.push({name: 'Abs', args: [], body: 'Return 1;'});
		const bf = path.join(dir, 'broken-fn.json');
		fs.writeFileSync(bf, JSON.stringify(broken));
		const out = path.join(dir, 'broken-fn.ahmi');
		const r = studio('--hmi-build', bf, '-o', out);

		assert.equal(r.status, 1, r.out + r.err);
		assert.match(r.out + r.err, /built-in function/);
		assert.equal(fs.existsSync(out), false);
	});

	// One that writes a tag acts, so an animation link may not call it.
	test('check refuses a writing QuickFunction in an animation link', () =>
	{
		const broken = JSON.parse(JSON.stringify(spec));
		broken.quickFunctions.push({name: 'Starter', args: [], body: 'Pump = 1;\nReturn 1;'});
		broken.pages[0].objects[0].links['percentFill.vertical'].expr = 'Starter()';
		const bf = path.join(dir, 'impure.json');
		fs.writeFileSync(bf, JSON.stringify(broken));
		const r = studio('--hmi-build', bf, '-o', path.join(dir, 'impure.ahmi'));

		assert.equal(r.status, 2, r.out + r.err);
		assert.match(r.out, /only be used in a script/);
	});

	// A range bound is an expression like any other: it used to be checked
	// nowhere, so an unknown tag in it reached the target PC unreported.
	test('check reports an unknown tag in a range bound', () =>
	{
		const broken = JSON.parse(JSON.stringify(spec));
		broken.pages[0].objects[0].links['percentFill.vertical'].pctMax = 'Nope';
		const bf = path.join(dir, 'broken-bound.json');
		fs.writeFileSync(bf, JSON.stringify(broken));
		const r = studio('--hmi-build', bf, '-o', path.join(dir, 'broken-bound.ahmi'));

		assert.equal(r.status, 2, r.out + r.err);
		assert.match(r.out, /unknown tag "Nope"/);
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

		// Also on objects and inside links
		const typo = JSON.parse(JSON.stringify(spec));
		typo.pages[0].objects[0].typ = 'rect';
		typo.pages[0].objects[0].links['percentFill.vertical'].pctMaxx = '90';
		const tf = path.join(dir, 'typo.json');
		fs.writeFileSync(tf, JSON.stringify(typo));
		const t = studio('--hmi-build', tf, '-o', path.join(dir, 'typo.ahmi'));

		assert.equal(t.status, 1);
		assert.match(t.err, /object tank: unknown field "typ"/);
		assert.match(t.err, /link percentFill.vertical: unknown field "pctMaxx"/);
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

	test('a data source password is wrapped, never written as it was given', () =>
	{
		const xml = fs.readFileSync(project, 'utf8');

		assert.ok(!xml.includes('"secret"') && !xml.includes('password="secret"'),
			'the spec password is in the project as it was typed');
		assert.match(xml, /password="hmi1:/);

		// A dump gives the wrapped form, so building from it does not wrap
		// twice and the round trip stays lossless
		const d = path.join(dir, 'pw.json');
		assert.equal(studio('--hmi-dump', project, '-o', d).status, 0);
		const dumped = JSON.parse(fs.readFileSync(d, 'utf8'));
		assert.match(dumped.dataSources[0].password, /^hmi1:/);
		assert.equal(dumped.dataSources[1].password, undefined, 'a SQLite source has no password');
	});

	test('the bind list, template and query sections survive the file', () =>
	{
		const d = path.join(dir, 'sql.json');
		assert.equal(studio('--hmi-dump', project, '-o', d).status, 0);
		const dumped = JSON.parse(fs.readFileSync(d, 'utf8'));

		assert.deepEqual(dumped.bindLists, [{name: 'BatchRow', items: [
			{tag: 'Level', column: 'Level'},
			{tag: 'Level', column: 'InAlarm', field: 'InAlarm'},
			{tag: 'Pump', column: 'Pump'}]}], 'a bare item is the tag into a column of its name');
		assert.equal(dumped.tableTemplates[0].columns[2].length, '80');
		assert.equal(dumped.tableTemplates[0].columns[2].allowNull, false);
		assert.equal(dumped.queries[0].sql, 'SELECT Level, Stamp\nFROM BatchLog\nORDER BY Stamp DESC',
			'the statement keeps its own line breaks');
		assert.equal(dumped.dataSources[0].options.legacyTls, true);
		assert.equal(dumped.dataSources[1].folder, 'documents',
			'a project names a folder, never a machine-specific path');
	});

	test('the SQL sections are checked', () =>
	{
		const cases = [
			[{dataSources: [{name: 'A', provider: 'oracle', host: 'h'}]},
				/not a kind of database/],
			[{dataSources: [{name: 'A', provider: 'sqlite', file: '/etc/passwd'}]},
				/cannot be an absolute path/],
			[{dataSources: [{name: 'A', provider: 'sqlite', file: '../up.db'}]},
				/cannot be an absolute path or contain/],
			[{dataSources: [{name: 'A', provider: 'mssql'}]}, /needs a server/],
			[{dataSources: [{name: 'A', provider: 'sqlite', file: 'a.db'},
				{name: 'a', provider: 'sqlite', file: 'b.db'}]}, /two of these named/],
			[{dataSources: [{name: 'A', provider: 'sqlite', file: 'a.db', typo: 1}]},
				/unknown field "typo"/],
			[{bindLists: [{name: 'L', items: []}]}, /has no tags in it/],
			[{bindLists: [{name: 'L', items: [{tag: 'Level', column: ''}]}]},
				/needs a column name/],
			[{bindLists: [{name: 'L', items: [{tag: 'Level', column: 'X'},
				{tag: 'Pump', column: 'x'}]}]}, /uses the column "x" twice/],
			[{bindLists: [{name: 'L', items: [{tag: 'Level', field: 'Nonsense', column: 'X'}]}]},
				/is not one of a tag's fields/],
			[{tableTemplates: [{name: 'T', columns: []}]}, /has no columns/],
			[{tableTemplates: [{name: 'T', columns: [{name: 'A', type: 'text', length: 'wide'}]}]},
				/must be a number of characters/],
			[{queries: [{name: 'Q', sql: '   '}]}, /has no statement/]
		];

		for (const [patch, expected] of cases)
		{
			const bad = JSON.parse(JSON.stringify(spec));
			delete bad.dataSources;
			delete bad.bindLists;
			delete bad.tableTemplates;
			delete bad.queries;
			Object.assign(bad, patch);

			const bf = path.join(dir, 'sqlbad.json');
			fs.writeFileSync(bf, JSON.stringify(bad));
			const r = studio('--hmi-build', bf, '-o', path.join(dir, 'sqlbad.ahmi'));

			assert.equal(r.status, 1, JSON.stringify(patch) + ' built anyway: ' + r.out + r.err);
			assert.match(r.err, expected);
		}
	});

	test('a missing file says so', () =>
	{
		const r = studio('--hmi-check', path.join(dir, 'nothere.ahmi'));

		assert.equal(r.status, 1);
		assert.match(r.err, /file not found: .*nothere\.ahmi/);
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
