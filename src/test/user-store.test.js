// Users changed at run time — exercises src/main/security/UserStore.js
import { test, describe, after } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'fs';
import os from 'os';
import path from 'path';
import { load, save, clear, storeFile, checkUsers } from '../main/security/UserStore.js';

const base = fs.mkdtempSync(path.join(os.tmpdir(), 'hmi-users-test-'));

after(() => fs.rmSync(base, {recursive: true, force: true}));

const user = (name, level) => ({name: name, level: level, salt: 'a'.repeat(32), hash: 'b'.repeat(64), iterations: 20000});

describe('user store', () =>
{
	test('none saved means the project users apply', async () =>
	{
		assert.equal(await load(base, 'Plant'), null);
	});

	test('saves and loads the list', async () =>
	{
		await save(base, 'Plant', [user('Operator', 100), user('Admin', 9999)]);
		assert.deepEqual((await load(base, 'Plant')).map(u => u.name + ':' + u.level), ['Operator:100', 'Admin:9999']);
		assert.deepEqual(fs.readdirSync(path.dirname(storeFile(base, 'Plant'))), ['Plant.json']);
	});

	test('refuses bad records, duplicates and plain passwords', () =>
	{
		assert.throws(() => checkUsers([user('A', 10000)]), /users/);
		assert.throws(() => checkUsers([user('A', 1), user('a', 2)]), /users/);
		assert.throws(() => checkUsers([{name: 'A', level: 1, password: 'secret'}]), /users/);
		assert.throws(() => checkUsers([user('../x', 1)]), /users/);
	});

	test('a damaged file falls back to the project users', async () =>
	{
		fs.writeFileSync(storeFile(base, 'Broken'), '{"users": [{"name": 1}]}');
		assert.equal(await load(base, 'Broken'), null);
	});

	test('clear removes the list', async () =>
	{
		await clear(base, 'Plant');
		assert.equal(await load(base, 'Plant'), null);
	});
});
