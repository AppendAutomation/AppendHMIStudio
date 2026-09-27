// Users changed at run time (ShowUserManager() in a Run): the whole list, kept
// in <userData>/users/<store>.json, which from then on replaces the project's
// users for that store. Only salted password hashes are ever stored. The
// renderer names a store, never a path; writes are atomic (temporary file and
// rename), as for retentive values.

import fs from 'fs';
import path from 'path';
import { storeName } from '../alarms/AlarmLog.js';

export const MAX_USERS = 1000;

export function storeFile(base, store)
{
	return path.join(base, 'users', storeName(store) + '.json');
}

export function checkUsers(users)
{
	if (!Array.isArray(users) || users.length > MAX_USERS)
	{
		throw new Error('bad arg: users');
	}

	const seen = new Set();

	return users.map((u) =>
	{
		const ok = u != null && typeof u.name === 'string' && /^[A-Za-z0-9][A-Za-z0-9 ._@-]{0,31}$/.test(u.name) &&
			Number.isInteger(u.level) && u.level >= 0 && u.level <= 9999 &&
			typeof u.salt === 'string' && /^[0-9a-f]{32}$/.test(u.salt) &&
			typeof u.hash === 'string' && /^[0-9a-f]{64}$/.test(u.hash) &&
			Number.isInteger(u.iterations) && u.iterations >= 1000 && u.iterations <= 1000000;

		if (!ok || seen.has(u.name.toLowerCase()))
		{
			throw new Error('bad arg: users');
		}

		seen.add(u.name.toLowerCase());

		return {name: u.name, level: u.level, salt: u.salt, hash: u.hash, iterations: u.iterations};
	});
}

// The saved list, or null when there is none (the project's users apply)
export async function load(base, store)
{
	let text;

	try
	{
		text = await fs.promises.readFile(storeFile(base, store), 'utf8');
	}
	catch (e)
	{
		return null;
	}

	try
	{
		return checkUsers(JSON.parse(text).users);
	}
	catch (e)
	{
		// A damaged list must not lock everyone out: the project's users apply
		return null;
	}
}

export async function save(base, store, users)
{
	const file = storeFile(base, store);
	const clean = checkUsers(users);
	await fs.promises.mkdir(path.dirname(file), {recursive: true});

	const tmp = file + '.' + process.pid + '.tmp';
	await fs.promises.writeFile(tmp, JSON.stringify({saved: new Date().toISOString(), users: clean}, null, '\t'), 'utf8');
	await fs.promises.rename(tmp, file);

	return clean.length;
}

export async function clear(base, store)
{
	await fs.promises.rm(storeFile(base, store), {force: true});
}
