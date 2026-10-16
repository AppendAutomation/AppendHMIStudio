// Per-PC credentials for a project's data sources, in
// <userData>/datasources/<store>.json.
//
// The project carries each data source, optionally with a password. A site
// that would rather the password did not travel with the project sets it here
// instead, on the machine that runs the application: an override wins over
// whatever the project holds. The renderer may say that a password is set and
// may set a new one, but can never read one back -- nothing here is ever sent
// to a window.
//
// Written to a temporary file and renamed into place, like the other stores,
// so a power cut leaves the old or the new file and never half of one.

import fs from 'fs';
import path from 'path';
import { storeName } from '../alarms/AlarmLog.js';
import * as secret from './Secret.js';

export const MAX_SOURCES = 100;
export const MAX_NAME = 64;
export const MAX_USER = 128;
export const MAX_PASSWORD = 256;

export function storeFile(base, store)
{
	return path.join(base, 'datasources', storeName(store) + '.json');
}

/** {name: {user, password}}; a password is stored wrapped, never plain. */
export function checkOverrides(overrides)
{
	if (overrides == null || typeof overrides !== 'object' || Array.isArray(overrides))
	{
		throw new Error('bad arg: overrides');
	}

	const names = Object.keys(overrides);

	if (names.length > MAX_SOURCES)
	{
		throw new Error('bad arg: overrides');
	}

	const out = {};

	for (const name of names)
	{
		const o = overrides[name];

		if (typeof name !== 'string' || name === '' || name.length > MAX_NAME ||
			o == null || typeof o !== 'object' || Array.isArray(o))
		{
			throw new Error('bad arg: overrides');
		}

		const user = (o.user == null) ? '' : String(o.user);
		const password = (o.password == null) ? '' : String(o.password);

		if (user.length > MAX_USER || password.length > MAX_PASSWORD)
		{
			throw new Error('bad arg: overrides');
		}

		out[name] = {user: user, password: secret.wrap(password)};
	}

	return out;
}

export async function load(base, store)
{
	try
	{
		const data = JSON.parse(await fs.promises.readFile(storeFile(base, store), 'utf8'));

		return checkOverrides(data.sources || {});
	}
	catch (e)
	{
		// None set on this PC: the project's own credentials are used
		return {};
	}
}

export async function save(base, store, overrides)
{
	const file = storeFile(base, store);
	const clean = checkOverrides(overrides);
	await fs.promises.mkdir(path.dirname(file), {recursive: true});

	const tmp = file + '.' + process.pid + '.tmp';
	await fs.promises.writeFile(tmp,
		JSON.stringify({saved: new Date().toISOString(), sources: clean}, null, '\t'), 'utf8');
	await fs.promises.rename(tmp, file);

	return Object.keys(clean).length;
}

export async function clear(base, store)
{
	await fs.promises.rm(storeFile(base, store), {force: true});
}

/**
 * What a window may know: which data sources have an override here, and
 * whether each carries a password. Never the password itself.
 */
export async function summary(base, store)
{
	const overrides = await load(base, store);

	return Object.keys(overrides).map((name) => ({name: name,
		user: overrides[name].user, hasPassword: overrides[name].password !== ''}));
}

/** Sets or removes one data source's override, keeping the rest. */
export async function setOverride(base, store, name, override)
{
	const overrides = await load(base, store);

	if (override == null)
	{
		delete overrides[name];
	}
	else
	{
		const one = {};
		one[name] = override;
		Object.assign(overrides, checkOverrides(one));
	}

	await save(base, store, overrides);

	return summary(base, store);
}

/**
 * A data source spec with this PC's credentials applied. An override's user or
 * password replaces the project's only when it has one, so a site can set just
 * the password and leave the user to the project.
 */
export function applyOverride(spec, overrides)
{
	const o = (overrides != null) ? overrides[spec.name] : null;

	if (o == null)
	{
		return spec;
	}

	const out = Object.assign({}, spec);

	if (o.user !== '')
	{
		out.user = o.user;
	}

	if (o.password !== '')
	{
		out.password = o.password;
	}

	return out;
}
