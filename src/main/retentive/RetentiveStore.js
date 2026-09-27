// Retentive tag values: the last value of each retentive memory tag, kept in
// <userData>/retentive/<store>.json so the next Run starts from it. The
// renderer names a store (the project, or a published runtime's product),
// never a path. Writes go to a temporary file first and are renamed into
// place, so a power cut leaves the old or the new file, never half of one.

import fs from 'fs';
import path from 'path';
import { storeName } from '../alarms/AlarmLog.js';

export const MAX_VALUES = 10000;

export function storeFile(base, store)
{
	return path.join(base, 'retentive', storeName(store) + '.json');
}

// Tag name -> number, string or boolean; anything else is refused
export function checkValues(values)
{
	if (values == null || typeof values !== 'object' || Array.isArray(values))
	{
		throw new Error('bad arg: values');
	}

	const names = Object.keys(values);

	if (names.length > MAX_VALUES)
	{
		throw new Error('bad arg: values');
	}

	const out = {};

	for (const name of names)
	{
		const v = values[name];

		if (typeof name !== 'string' || name.length > 63 ||
			!(typeof v === 'number' && Number.isFinite(v) || typeof v === 'boolean' ||
				typeof v === 'string' && v.length <= 2000))
		{
			throw new Error('bad arg: values');
		}

		out[name] = v;
	}

	return out;
}

export async function load(base, store)
{
	try
	{
		const data = JSON.parse(await fs.promises.readFile(storeFile(base, store), 'utf8'));

		return checkValues(data.values || {});
	}
	catch (e)
	{
		// None saved yet, or unreadable: start from the initial values
		return {};
	}
}

export async function save(base, store, values)
{
	const file = storeFile(base, store);
	const clean = checkValues(values);
	await fs.promises.mkdir(path.dirname(file), {recursive: true});

	const tmp = file + '.' + process.pid + '.tmp';
	await fs.promises.writeFile(tmp, JSON.stringify({saved: new Date().toISOString(), values: clean}, null, '\t'), 'utf8');
	await fs.promises.rename(tmp, file);

	return Object.keys(clean).length;
}

export async function clear(base, store)
{
	await fs.promises.rm(storeFile(base, store), {force: true});
}
