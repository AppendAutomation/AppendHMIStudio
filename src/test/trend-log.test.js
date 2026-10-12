// The trend history files — exercises src/main/trends/TrendLog.js
import { test, describe, after } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'fs';
import os from 'os';
import path from 'path';
import { storeDir, append, query, prune, decimate, checkSamples, TrendRecorders,
	COLUMNS } from '../main/trends/TrendLog.js';

const tempDirs = [];

function tempDir()
{
	const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'hmi-trend-test-'));
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

const at = (y, m, d, h, min) => new Date(y, m - 1, d, h ?? 12, min || 0, 0, 0).getTime();

describe('samples', () =>
{
	test('are checked', () =>
	{
		assert.deepEqual(checkSamples([[1000.4, 'TT101', 2.5], [2000, 'Run', null]]),
			[[1000, 'TT101', 2.5], [2000, 'Run', null]]);
		assert.throws(() => checkSamples('x'), /bad arg/);
		assert.throws(() => checkSamples([[1, '../x', 1]]), /bad arg/);
		assert.throws(() => checkSamples([[1, 'A', 'text']]), /bad arg/);
		assert.throws(() => checkSamples([[NaN, 'A', 1]]), /bad arg/);
		assert.throws(() => checkSamples([[1, 'A', Infinity]]), /bad arg/);
	});

	test('go to one file per day, with a header', async () =>
	{
		const base = tempDir();
		await append(base, 'Line 1', [[at(2026, 10, 1, 23, 59), 'A', 1], [at(2026, 10, 2, 0, 1), 'A', 2],
			[at(2026, 10, 2, 0, 2), 'A', null]]);
		await append(base, 'Line 1', [[at(2026, 10, 2, 0, 3), 'A', 3]]);

		const dir = storeDir(base, 'Line 1');
		assert.deepEqual(fs.readdirSync(dir).sort(), ['trends-2026-10-01.csv', 'trends-2026-10-02.csv']);

		const lines = fs.readFileSync(path.join(dir, 'trends-2026-10-02.csv'), 'utf8').split('\r\n');
		assert.equal(lines[0], COLUMNS.join(','));
		assert.equal(lines[1], at(2026, 10, 2, 0, 1) + ',A,2');
		assert.equal(lines[2], at(2026, 10, 2, 0, 2) + ',A,');
		assert.equal(lines[3], at(2026, 10, 2, 0, 3) + ',A,3');
	});
});

describe('query', () =>
{
	test('returns the range, the points either side and gaps, for the tags asked', async () =>
	{
		const base = tempDir();
		await append(base, 's', [[at(2026, 10, 1, 22), 'A', 1], [at(2026, 10, 2, 1), 'a', 2],
			[at(2026, 10, 2, 2), 'B', 9], [at(2026, 10, 2, 3), 'A', null], [at(2026, 10, 2, 4), 'A', 4],
			[at(2026, 10, 2, 9), 'A', 5]]);

		const r = await query(base, 's', ['A', 'C'], at(2026, 10, 2, 0, 30), at(2026, 10, 2, 5));
		assert.deepEqual(r.A, [[at(2026, 10, 1, 22), 1], [at(2026, 10, 2, 1), 2], [at(2026, 10, 2, 3), null],
			[at(2026, 10, 2, 4), 4], [at(2026, 10, 2, 9), 5]]);
		assert.deepEqual(r.C, []);
	});

	test('of a store with no files is empty', async () =>
	{
		assert.deepEqual(await query(tempDir(), 's', ['A'], 0, 1000), {A: []});
	});

	test('refuses bad arguments', async () =>
	{
		await assert.rejects(query(tempDir(), 's', ['../x'], 0, 1), /bad arg/);
		await assert.rejects(query(tempDir(), 's', ['A'], 10, 1), /bad arg/);
	});

	test('thins out long ranges, keeping the peaks and gaps', () =>
	{
		const points = [];

		for (let i = 0; i < 10000; i++)
		{
			points.push([i, (i === 5000) ? 100 : ((i === 7000) ? null : Math.sin(i / 100))]);
		}

		const out = decimate(points, 0, 10000, 400);
		assert.ok(out.length <= 400 * 1.1, String(out.length));
		assert.ok(out.some(p => p[1] === 100));
		assert.ok(out.some(p => p[1] === null && p[0] === 7000));
		assert.deepEqual(out[0], points[0]);
		assert.deepEqual(out[out.length - 1], points[points.length - 1]);

		for (let i = 1; i < out.length; i++)
		{
			assert.ok(out[i][0] > out[i - 1][0]);
		}
	});
});

describe('prune', () =>
{
	test('removes old days, then the oldest over the size limit, never today', async () =>
	{
		const base = tempDir();
		const now = at(2026, 10, 12);

		for (const d of [1, 8, 9, 10, 11, 12])
		{
			await append(base, 's', [[at(2026, 10, d), 'A', 1234567.891]]);
		}

		const size = fs.statSync(path.join(storeDir(base, 's'), 'trends-2026-10-12.csv')).size;
		const removed = await prune(base, 's', now, 7, size * 3);
		assert.deepEqual(removed, ['trends-2026-10-01.csv', 'trends-2026-10-08.csv', 'trends-2026-10-09.csv']);
		assert.deepEqual(fs.readdirSync(storeDir(base, 's')).sort(),
			['trends-2026-10-10.csv', 'trends-2026-10-11.csv', 'trends-2026-10-12.csv']);

		await prune(base, 's', now, 7, 1);
		assert.deepEqual(fs.readdirSync(storeDir(base, 's')), ['trends-2026-10-12.csv']);
	});
});

describe('recorders (Append HMI Web)', () =>
{
	test('one browser records a store until it goes quiet', () =>
	{
		const r = new TrendRecorders(1000);
		const a = {};
		const b = {};

		assert.equal(r.accept(a, 's', 0), true);
		assert.equal(r.accept(b, 's', 500), false);
		assert.equal(r.accept(b, 't', 500), true);
		assert.equal(r.accept(a, 's', 900), true);
		assert.equal(r.accept(b, 's', 1800), false);
		assert.equal(r.accept(b, 's', 1901), true);
		r.release(b);
		assert.equal(r.accept(a, 's', 1902), true);
	});
});
