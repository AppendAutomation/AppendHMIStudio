// The alarm history on disk: one CSV file per day under
// <userData>/alarms/<store>/alarms-YYYY-MM-DD.csv, kept RETENTION_DAYS days.
// The renderer names a store (the project, or a published runtime's product),
// never a path; main builds every path here.

import fs from 'fs';
import path from 'path';

export const RETENTION_DAYS = 90;
export const MAX_EVENTS = 1000;
export const COLUMNS = ['Time', 'Event', 'Tag', 'Description', 'Condition', 'Value', 'Limit'];

const FILE_RE = /^alarms-(\d{4})-(\d{2})-(\d{2})\.csv$/;
const FIELDS = ['event', 'tag', 'description', 'condition', 'value', 'limit'];

// A store name usable as one folder name, whatever the renderer sent
export function storeName(store)
{
	const name = String(store == null ? '' : store).replace(/[^A-Za-z0-9 _-]/g, '_').trim().slice(0, 64);

	if (!name || /^[ ._-]*$/.test(name))
	{
		throw new Error('bad arg: store');
	}

	return name;
}

export function storeDir(base, store)
{
	return path.join(base, 'alarms', storeName(store));
}

function pad(n, w)
{
	return String(n).padStart(w || 2, '0');
}

function dayKey(d)
{
	return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate());
}

// Local time, sortable and readable in a spreadsheet
export function formatTime(ms)
{
	const d = new Date(ms);

	return dayKey(d) + ' ' + pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' +
		pad(d.getSeconds()) + '.' + pad(d.getMilliseconds(), 3);
}

export function parseTime(text)
{
	const m = /^(\d{4})-(\d{2})-(\d{2}) (\d{2}):(\d{2}):(\d{2})(?:\.(\d{3}))?$/.exec(text || '');

	return m ? new Date(+m[1], m[2] - 1, +m[3], +m[4], +m[5], +m[6], +(m[7] || 0)).getTime() : NaN;
}

function csvCell(v)
{
	const s = (v == null) ? '' : String(v);

	return /[",\r\n]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s;
}

// CSV rows (RFC 4180 quoting, quoted fields may hold newlines)
export function parseCsv(text)
{
	const rows = [];
	let row = [];
	let cell = '';
	let quoted = false;

	for (let i = 0; i < text.length; i++)
	{
		const c = text[i];

		if (quoted)
		{
			if (c === '"' && text[i + 1] === '"')
			{
				cell += '"';
				i++;
			}
			else if (c === '"')
			{
				quoted = false;
			}
			else
			{
				cell += c;
			}
		}
		else if (c === '"')
		{
			quoted = true;
		}
		else if (c === ',')
		{
			row.push(cell);
			cell = '';
		}
		else if (c === '\n' || c === '\r')
		{
			if (c === '\r' && text[i + 1] === '\n')
			{
				i++;
			}

			row.push(cell);
			rows.push(row);
			row = [];
			cell = '';
		}
		else
		{
			cell += c;
		}
	}

	if (cell !== '' || row.length > 0)
	{
		row.push(cell);
		rows.push(row);
	}

	return rows;
}

// Checks events from the renderer: an array of {time, event, tag, ...}
export function checkEvents(events)
{
	if (!Array.isArray(events) || events.length > MAX_EVENTS)
	{
		throw new Error('bad arg: events');
	}

	return events.map((e) =>
	{
		if (e == null || typeof e !== 'object' || !Number.isFinite(e.time))
		{
			throw new Error('bad arg: events');
		}

		const out = {time: e.time};

		for (const f of FIELDS)
		{
			out[f] = (e[f] == null) ? '' : String(e[f]).slice(0, 500);
		}

		return out;
	});
}

export async function append(base, store, events)
{
	const list = checkEvents(events);
	const dir = storeDir(base, store);
	await fs.promises.mkdir(dir, {recursive: true});

	const byFile = new Map();

	for (const e of list)
	{
		const file = path.join(dir, 'alarms-' + dayKey(new Date(e.time)) + '.csv');

		if (!byFile.has(file))
		{
			byFile.set(file, []);
		}

		byFile.get(file).push([formatTime(e.time)].concat(FIELDS.map(f => e[f])).map(csvCell).join(',') + '\r\n');
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

	return names.filter(n => FILE_RE.test(n)).sort().reverse();
}

// The newest events, newest first, at most limit
export async function recent(base, store, limit)
{
	const max = Math.max(1, Math.min(Number.isInteger(limit) ? limit : 200, 10000));
	const dir = storeDir(base, store);
	const out = [];

	for (const name of await dayFiles(dir))
	{
		const rows = parseCsv(await fs.promises.readFile(path.join(dir, name), 'utf8'));

		for (let i = rows.length - 1; i >= 1 && out.length < max; i--)
		{
			const r = rows[i];

			if (r.length < COLUMNS.length)
			{
				continue;
			}

			const e = {time: parseTime(r[0])};
			FIELDS.forEach((f, j) => { e[f] = r[j + 1]; });
			out.push(e);
		}

		if (out.length >= max)
		{
			break;
		}
	}

	return out;
}

// Deletes day files older than days (by their date, not their mtime)
export async function prune(base, store, now, days)
{
	const dir = storeDir(base, store);
	const keep = days || RETENTION_DAYS;
	const cutoff = new Date(now || Date.now());
	cutoff.setHours(0, 0, 0, 0);
	cutoff.setDate(cutoff.getDate() - keep);
	const removed = [];

	for (const name of await dayFiles(dir))
	{
		const m = FILE_RE.exec(name);

		if (new Date(+m[1], m[2] - 1, +m[3]).getTime() < cutoff.getTime())
		{
			await fs.promises.rm(path.join(dir, name), {force: true});
			removed.push(name);
		}
	}

	return removed;
}
