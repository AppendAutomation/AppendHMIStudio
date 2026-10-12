// The trend history on disk: one CSV file per day under
// <userData>/trends/<store>/trends-YYYY-MM-DD.csv, with the columns
// Time (milliseconds since 1970, UTC), Tag and Value (empty for a gap: bad
// quality, or the Run stopped). Kept RETENTION_DAYS days and at most
// MAX_BYTES per store, the oldest day going first. As for the alarm history,
// the renderer names a store and main builds every path.

import fs from 'fs';
import path from 'path';
import {storeName} from '../alarms/AlarmLog.js';

export {storeName};

export const RETENTION_DAYS = 7;
export const MAX_BYTES = 500 * 1024 * 1024;
export const MAX_SAMPLES = 50000;
export const MAX_POINTS = 4000;
export const COLUMNS = ['Time', 'Tag', 'Value'];

const FILE_RE = /^trends-(\d{4})-(\d{2})-(\d{2})\.csv$/;
const TAG_RE = /^[A-Za-z_$][A-Za-z0-9_$]{0,62}$/;
const DAY_MS = 24 * 3600 * 1000;

export function storeDir(base, store)
{
	return path.join(base, 'trends', storeName(store));
}

function pad(n)
{
	return String(n).padStart(2, '0');
}

function dayKey(d)
{
	return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate());
}

function fileFor(dir, ms)
{
	return path.join(dir, 'trends-' + dayKey(new Date(ms)) + '.csv');
}

// Checks samples from the renderer: [[time, tag, value or null], ...]
export function checkSamples(samples)
{
	if (!Array.isArray(samples) || samples.length > MAX_SAMPLES)
	{
		throw new Error('bad arg: samples');
	}

	return samples.map((s) =>
	{
		if (!Array.isArray(s) || !Number.isFinite(s[0]) || typeof s[1] !== 'string' || !TAG_RE.test(s[1]) ||
			!(s[2] == null || Number.isFinite(s[2])))
		{
			throw new Error('bad arg: samples');
		}

		return [Math.round(s[0]), s[1], (s[2] == null) ? null : s[2]];
	});
}

export async function append(base, store, samples)
{
	const list = checkSamples(samples);
	const dir = storeDir(base, store);
	await fs.promises.mkdir(dir, {recursive: true});

	const byFile = new Map();

	for (const s of list)
	{
		const file = fileFor(dir, s[0]);

		if (!byFile.has(file))
		{
			byFile.set(file, []);
		}

		byFile.get(file).push(s[0] + ',' + s[1] + ',' + ((s[2] == null) ? '' : s[2]) + '\r\n');
	}

	for (const [file, lines] of byFile)
	{
		let head = '';

		try
		{
			await fs.promises.access(file);
		}
		catch (e)
		{
			head = COLUMNS.join(',') + '\r\n';
		}

		await fs.promises.appendFile(file, head + lines.join(''), 'utf8');
	}

	return list.length;
}

async function dayFiles(dir)
{
	let names;

	try
	{
		names = await fs.promises.readdir(dir);
	}
	catch (e)
	{
		return [];
	}

	return names.filter(n => FILE_RE.test(n)).sort();
}

// Rows of one day file for the wanted tags (lower case -> name as asked)
async function readDay(file, wanted, out)
{
	let text;

	try
	{
		text = await fs.promises.readFile(file, 'utf8');
	}
	catch (e)
	{
		return;
	}

	let start = 0;

	while (start < text.length)
	{
		let end = text.indexOf('\n', start);

		if (end < 0)
		{
			end = text.length;
		}

		const line = text.slice(start, (text[end - 1] === '\r') ? end - 1 : end);
		start = end + 1;

		const a = line.indexOf(',');
		const b = (a >= 0) ? line.indexOf(',', a + 1) : -1;

		if (b < 0)
		{
			continue;
		}

		const name = wanted.get(line.slice(a + 1, b).toLowerCase());
		const t = Number(line.slice(0, a));

		if (name == null || !Number.isFinite(t))
		{
			continue;
		}

		const v = line.slice(b + 1);
		out.get(name).push([t, (v === '') ? null : Number(v)]);
	}
}

// At most max points: the first, lowest, highest and last of each time
// bucket, and the gaps, in time order
export function decimate(points, from, to, max)
{
	if (points.length <= max)
	{
		return points;
	}

	const buckets = Math.max(1, Math.floor(max / 4));
	const width = Math.max(1, (to - from) / buckets);
	const out = [];
	let i = 0;

	while (i < points.length)
	{
		const k = Math.floor((points[i][0] - from) / width);
		const group = [];

		while (i < points.length && Math.floor((points[i][0] - from) / width) === k)
		{
			group.push(points[i++]);
		}

		let lo = null;
		let hi = null;
		const keep = new Set([group[0], group[group.length - 1]]);

		for (const p of group)
		{
			if (p[1] == null)
			{
				keep.add(p);
			}
			else
			{
				lo = (lo == null || p[1] < lo[1]) ? p : lo;
				hi = (hi == null || p[1] > hi[1]) ? p : hi;
			}
		}

		if (lo != null) { keep.add(lo); }
		if (hi != null) { keep.add(hi); }

		for (const p of group)
		{
			if (keep.has(p))
			{
				out.push(p);
			}
		}
	}

	return out;
}

// {tag: [[time, value or null], ...]} for each tag from `from` to `to`, with
// the last point before `from` and the first after `to`, so a line reaches
// both edges
export async function query(base, store, tags, from, to, maxPoints)
{
	if (!Array.isArray(tags) || tags.length === 0 || tags.length > 64 || !tags.every(t => typeof t === 'string' &&
		TAG_RE.test(t)) || !Number.isFinite(from) || !Number.isFinite(to) || to < from)
	{
		throw new Error('bad arg: query');
	}

	const max = Math.max(100, Math.min(Number.isInteger(maxPoints) ? maxPoints : MAX_POINTS, 20000));
	const dir = storeDir(base, store);
	const files = new Set(await dayFiles(dir));
	const wanted = new Map(tags.map(t => [t.toLowerCase(), t]));
	const all = new Map(tags.map(t => [t, []]));

	// The day before `from` too, for the points that come before it
	for (let day = new Date(from - DAY_MS); ; day = new Date(day.getFullYear(), day.getMonth(), day.getDate() + 1))
	{
		const name = 'trends-' + dayKey(day) + '.csv';

		if (files.has(name))
		{
			await readDay(path.join(dir, name), wanted, all);
		}

		if (dayKey(day) >= dayKey(new Date(to)))
		{
			break;
		}
	}

	const out = {};

	for (const [tag, points] of all)
	{
		points.sort((p, q) => p[0] - q[0]);
		let first = 0;

		while (first < points.length && points[first][0] < from)
		{
			first++;
		}

		let last = first;

		while (last < points.length && points[last][0] <= to)
		{
			last++;
		}

		const inRange = decimate(points.slice(first, last), from, to, max);
		out[tag] = ((first > 0) ? [points[first - 1]] : []).concat(inRange, (last < points.length) ? [points[last]] : []);
	}

	return out;
}

// Deletes day files older than days, then the oldest until the store is at
// most maxBytes (today's file is kept)
export async function prune(base, store, now, days, maxBytes)
{
	const dir = storeDir(base, store);
	const cutoff = new Date(now || Date.now());
	cutoff.setHours(0, 0, 0, 0);
	cutoff.setDate(cutoff.getDate() - (days || RETENTION_DAYS));
	const today = 'trends-' + dayKey(new Date(now || Date.now())) + '.csv';
	const removed = [];
	const kept = [];

	for (const name of await dayFiles(dir))
	{
		const m = FILE_RE.exec(name);
		const file = path.join(dir, name);

		if (new Date(+m[1], m[2] - 1, +m[3]).getTime() < cutoff.getTime())
		{
			await fs.promises.rm(file, {force: true});
			removed.push(name);
		}
		else
		{
			let size = 0;

			try
			{
				size = (await fs.promises.stat(file)).size;
			}
			catch (e)
			{
				// Gone meanwhile
			}

			kept.push({name, file, size});
		}
	}

	let total = kept.reduce((sum, f) => sum + f.size, 0);
	const limit = maxBytes || MAX_BYTES;

	for (const f of kept)
	{
		if (total <= limit || f.name >= today)
		{
			break;
		}

		await fs.promises.rm(f.file, {force: true});
		removed.push(f.name);
		total -= f.size;
	}

	return removed;
}

// Append HMI Web: every browser runs the application and samples its trends,
// but one history is written. The first browser to report a store records
// it; another takes over when that one has been quiet for ttlMs.
export class TrendRecorders
{
	constructor(ttlMs)
	{
		this.ttlMs = ttlMs || 15000;
		this.stores = new Map();
	}

	accept(owner, store, now)
	{
		const time = now || Date.now();
		const s = this.stores.get(store);

		if (s != null && s.owner !== owner && time - s.time < this.ttlMs)
		{
			return false;
		}

		this.stores.set(store, {owner, time});

		return true;
	}

	release(owner)
	{
		for (const [store, s] of this.stores)
		{
			if (s.owner === owner)
			{
				this.stores.delete(store);
			}
		}
	}
}
