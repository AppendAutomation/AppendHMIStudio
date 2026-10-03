// Recipes saved at run time — exercises src/main/recipes/RecipeStore.js
import { test, describe, after } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'fs';
import os from 'os';
import path from 'path';
import { load, save, clear, storeFile, checkRecipes, exportCsv, importCsv, MAX_CSV } from '../main/recipes/RecipeStore.js';

const base = fs.mkdtempSync(path.join(os.tmpdir(), 'hmi-recipes-test-'));

after(() => fs.rmSync(base, {recursive: true, force: true}));

describe('recipe store', () =>
{
	test('none saved means the project recipes apply', async () =>
	{
		assert.equal(await load(base, 'Plant'), null);
	});

	test('saves and loads every book', async () =>
	{
		const books = {Plasticizers: {'Batch A': {Plast1_Pct: 60, Name: 'A, "quoted"'}, Empty: {}}, Other: {}};
		assert.equal(await save(base, 'Plant', books), 2);
		assert.deepEqual(await load(base, 'Plant'), books);
		assert.deepEqual(fs.readdirSync(path.dirname(storeFile(base, 'Plant'))), ['Plant.json']);
	});

	test('refuses bad names, values and shapes', () =>
	{
		for (const bad of [null, [], {'': {}}, {B: []}, {B: {'': {}}}, {B: {r: {t: {}}}}, {B: {r: {t: NaN}}},
			{B: {r: {t: 'x'.repeat(5000)}}}, {B: {r: {'a\nb': 1}}}, {B: {r: {['x'.repeat(65)]: 1}}}])
		{
			assert.throws(() => checkRecipes(bad), /bad arg: recipes/, JSON.stringify(bad));
		}

		assert.deepEqual(checkRecipes({B: {r: {t: 1.5, s: ''}}}), {B: {r: {t: 1.5, s: ''}}});
	});

	test('a damaged file falls back to the project', async () =>
	{
		fs.writeFileSync(storeFile(base, 'Broken'), '{"books": ');
		assert.equal(await load(base, 'Broken'), null);
	});

	test('store names are never paths', async () =>
	{
		assert.equal(path.dirname(storeFile(base, '../../escape')), path.join(base, 'recipes'));
		await assert.rejects(save(base, '..', {}), /bad arg: store/);
	});

	test('clear forgets the saved recipes', async () =>
	{
		await clear(base, 'Plant');
		assert.equal(await load(base, 'Plant'), null);
		await clear(base, 'Plant');
	});
});

describe('recipe CSV files', () =>
{
	const fakeDialog = (file) => ({
		showSaveDialog: async () => file == null ? {canceled: true} : {canceled: false, filePath: file},
		showOpenDialog: async () => file == null ? {canceled: true, filePaths: []} : {canceled: false, filePaths: [file]}
	});

	test('export writes the chosen file, adding .csv', async () =>
	{
		const target = path.join(base, 'export-a');
		const file = await exportCsv(fakeDialog(target), null, 'Batch A', 'Tag,Value\r\nT,1\r\n');
		assert.equal(file, target + '.csv');
		assert.equal(fs.readFileSync(file, 'utf8'), 'Tag,Value\r\nT,1\r\n');
	});

	test('cancel gives null', async () =>
	{
		assert.equal(await exportCsv(fakeDialog(null), null, 'x', 'a'), null);
		assert.equal(await importCsv(fakeDialog(null), null), null);
	});

	test('import reads the chosen file', async () =>
	{
		const file = path.join(base, 'export-a.csv');
		assert.deepEqual(await importCsv(fakeDialog(file), null), {file: file, text: 'Tag,Value\r\nT,1\r\n'});
	});

	test('refuses oversized text and files', async () =>
	{
		await assert.rejects(exportCsv(fakeDialog(path.join(base, 'big')), null, 'x', 'x'.repeat(MAX_CSV + 1)), /bad arg/);
		const big = path.join(base, 'big.csv');
		fs.writeFileSync(big, 'x'.repeat(MAX_CSV + 1));
		await assert.rejects(importCsv(fakeDialog(big), null), /too large/);
	});
});
