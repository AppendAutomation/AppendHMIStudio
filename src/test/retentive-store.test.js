// Retentive tag values on disk — exercises src/main/retentive/RetentiveStore.js
import { test, describe, after } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'fs';
import os from 'os';
import path from 'path';
import { load, save, clear, storeFile, checkValues } from '../main/retentive/RetentiveStore.js';

const base = fs.mkdtempSync(path.join(os.tmpdir(), 'hmi-retentive-test-'));

after(() => fs.rmSync(base, {recursive: true, force: true}));

describe('retentive store', () =>
{
	test('nothing saved loads as no values', async () =>
	{
		assert.deepEqual(await load(base, 'Plant'), {});
	});

	test('saves and loads numbers, text and booleans', async () =>
	{
		await save(base, 'Plant', {Setpoint: 42.5, Recipe: 'Batch A', Enabled: true});
		assert.deepEqual(await load(base, 'Plant'), {Setpoint: 42.5, Recipe: 'Batch A', Enabled: true});
		assert.equal(path.basename(storeFile(base, 'Plant')), 'Plant.json');
		assert.deepEqual(fs.readdirSync(path.dirname(storeFile(base, 'Plant'))), ['Plant.json'], 'no temp file left');
	});

	test('a later save replaces the values', async () =>
	{
		await save(base, 'Plant', {Setpoint: 10});
		assert.deepEqual(await load(base, 'Plant'), {Setpoint: 10});
	});

	test('the store name never becomes a path', () =>
	{
		assert.equal(path.dirname(storeFile(base, '../../x')), path.join(base, 'retentive'));
	});

	test('refuses values that are not plain', () =>
	{
		assert.throws(() => checkValues([1]), /values/);
		assert.throws(() => checkValues({a: {b: 1}}), /values/);
		assert.throws(() => checkValues({a: NaN}), /values/);
	});

	test('a damaged file starts from the initial values', async () =>
	{
		fs.writeFileSync(storeFile(base, 'Broken'), '{not json');
		assert.deepEqual(await load(base, 'Broken'), {});
	});

	test('clear removes the values', async () =>
	{
		await clear(base, 'Plant');
		assert.deepEqual(await load(base, 'Plant'), {});
	});
});
