// The alarm history files — exercises src/main/alarms/AlarmLog.js
import { test, describe, after } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'fs';
import os from 'os';
import path from 'path';
import { storeName, storeDir, append, recent, prune, parseCsv, formatTime, parseTime,
	checkEvents, COLUMNS } from '../main/alarms/AlarmLog.js';

const tempDirs = [];

function tempDir()
{
	const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'hmi-alarm-test-'));
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

const day = (y, m, d, h) => new Date(y, m - 1, d, h || 12, 0, 0, 0).getTime();

describe('store names', () =>
{
	test('become one safe folder name', () =>
	{
		assert.equal(storeName('Line 3 HMI'), 'Line 3 HMI');
		assert.equal(storeName('../../etc'), '______etc');
		assert.equal(storeName('a/b\\c:d'), 'a_b_c_d');
		assert.equal(path.dirname(storeDir('/base', '../../x')), path.join('/base', 'alarms'));
	});

	test('refuse empty or dots-only names', () =>
	{
		assert.throws(() => storeName(''), /store/);
		assert.throws(() => storeName('..'), /store/);
		assert.throws(() => storeName(null), /store/);
	});
});

describe('events', () =>
{
	test('are checked and trimmed', () =>
	{
		assert.throws(() => checkEvents('x'), /events/);
		assert.throws(() => checkEvents([{event: 'ALM'}]), /events/);
		const [e] = checkEvents([{time: 1, event: 'ALM', tag: 'T', value: 12.5, extra: 'no'}]);
		assert.deepEqual(Object.keys(e), ['time', 'event', 'tag', 'description', 'condition', 'value', 'limit']);
		assert.equal(e.value, '12.5');
	});

	test('times round-trip in local time', () =>
	{
		const t = day(2026, 9, 27, 14) + 123;
		assert.equal(parseTime(formatTime(t)), t);
	});
});

describe('files', () =>
{
	test('one CSV per day with a header, quoted where needed', async () =>
	{
		const base = tempDir();
		await append(base, 'Plant', [
			{time: day(2026, 9, 26), event: 'ALM', tag: 'Tank', description: 'Tank level, "high"', condition: 'high', value: 91, limit: 90},
			{time: day(2026, 9, 27), event: 'RTN', tag: 'Tank', description: 'Tank level', condition: 'high', value: 80, limit: 90}
		]);
		await append(base, 'Plant', [{time: day(2026, 9, 27, 13), event: 'ACK', tag: 'Tank'}]);

		const dir = storeDir(base, 'Plant');
		assert.deepEqual(fs.readdirSync(dir).sort(), ['alarms-2026-09-26.csv', 'alarms-2026-09-27.csv']);

		const rows = parseCsv(fs.readFileSync(path.join(dir, 'alarms-2026-09-27.csv'), 'utf8'));
		assert.deepEqual(rows[0], COLUMNS);
		assert.equal(rows.length, 3, 'header once, two events');

		const first = parseCsv(fs.readFileSync(path.join(dir, 'alarms-2026-09-26.csv'), 'utf8'));
		assert.equal(first[1][3], 'Tank level, "high"');
	});

	test('recent reads newest first across days, up to the limit', async () =>
	{
		const base = tempDir();
		const events = [];

		for (let i = 0; i < 5; i++)
		{
			events.push({time: day(2026, 9, 20 + i), event: 'ALM', tag: 'T' + i});
		}

		await append(base, 'P', events);
		const r = await recent(base, 'P', 3);

		assert.deepEqual(r.map(e => e.tag), ['T4', 'T3', 'T2']);
		assert.equal(r[0].time, day(2026, 9, 24));
		assert.deepEqual(await recent(base, 'Nothing here', 5), []);
	});

	test('prune removes day files older than the retention', async () =>
	{
		const base = tempDir();
		await append(base, 'P', [{time: day(2026, 6, 1), event: 'ALM'}, {time: day(2026, 6, 30), event: 'ALM'},
			{time: day(2026, 9, 27), event: 'ALM'}]);

		const removed = await prune(base, 'P', day(2026, 9, 27), 90);

		assert.deepEqual(removed, ['alarms-2026-06-01.csv']);
		assert.deepEqual(fs.readdirSync(storeDir(base, 'P')).sort(),
			['alarms-2026-06-30.csv', 'alarms-2026-09-27.csv']);
	});
});
