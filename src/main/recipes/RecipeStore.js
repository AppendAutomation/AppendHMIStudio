// Recipes saved at run time (RecipeSave, RecipeRename, RecipeDelete,
// RecipeImport in a Run): every book's recipes, kept in
// <userData>/recipes/<store>.json. A book saved here replaces the project's
// starting recipes for that book. The renderer names a store, never a path;
// writes are atomic (temporary file and rename), as for retentive values.
//
// Also the CSV files of RecipeExport and RecipeImport, chosen in the OS
// dialog and written or read here: the renderer's generic file IPC accepts
// diagram formats only.

import fs from 'fs';
import path from 'path';
import { storeName } from '../alarms/AlarmLog.js';

export const MAX_BOOKS = 200;
export const MAX_RECIPES = 5000;
export const MAX_VALUES = 2000;
export const MAX_TEXT = 4096;
export const MAX_CSV = 1024 * 1024;

export function storeFile(base, store)
{
	return path.join(base, 'recipes', storeName(store) + '.json');
}

// Book, recipe and tag names: 1 to 64 characters, no control characters
export function validName(name)
{
	return typeof name === 'string' && name.trim() !== '' && name.length <= 64 && !/[\x00-\x1f\x7f]/.test(name);
}

// {book: {recipe: {tag: value}}}, values numbers or text
export function checkRecipes(books)
{
	if (books == null || typeof books !== 'object' || Array.isArray(books) || Object.keys(books).length > MAX_BOOKS)
	{
		throw new Error('bad arg: recipes');
	}

	const out = {};
	let count = 0;

	for (const [book, recipes] of Object.entries(books))
	{
		if (!validName(book) || recipes == null || typeof recipes !== 'object' || Array.isArray(recipes))
		{
			throw new Error('bad arg: recipes');
		}

		out[book] = {};

		for (const [name, values] of Object.entries(recipes))
		{
			if (!validName(name) || values == null || typeof values !== 'object' || Array.isArray(values) ||
				Object.keys(values).length > MAX_VALUES || ++count > MAX_RECIPES)
			{
				throw new Error('bad arg: recipes');
			}

			out[book][name] = {};

			for (const [tag, v] of Object.entries(values))
			{
				const ok = validName(tag) && ((typeof v === 'number' && Number.isFinite(v)) ||
					(typeof v === 'string' && v.length <= MAX_TEXT));

				if (!ok)
				{
					throw new Error('bad arg: recipes');
				}

				out[book][name][tag] = v;
			}
		}
	}

	return out;
}

// The saved books, or null when there are none (the project's apply)
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
		return checkRecipes(JSON.parse(text).books);
	}
	catch (e)
	{
		return null;
	}
}

export async function save(base, store, books)
{
	const file = storeFile(base, store);
	const clean = checkRecipes(books);
	await fs.promises.mkdir(path.dirname(file), {recursive: true});

	const tmp = file + '.' + process.pid + '.tmp';
	await fs.promises.writeFile(tmp, JSON.stringify({saved: new Date().toISOString(), books: clean}, null, '\t'), 'utf8');
	await fs.promises.rename(tmp, file);

	return Object.keys(clean).length;
}

export async function clear(base, store)
{
	await fs.promises.rm(storeFile(base, store), {force: true});
}

function csvFileName(name)
{
	const s = String(name || 'recipe').replace(/[\x00-\x1f<>:"/\\|?*]/g, '').replace(/[. ]+$/, '').trim();

	return (s || 'recipe').slice(0, 100) + '.csv';
}

// RecipeExport: asks where, then writes. Resolves with the file, or null
// when the operator cancels. dialog is Electron's, win the parent window.
export async function exportCsv(dialog, win, defaultName, text)
{
	if (typeof text !== 'string' || text.length > MAX_CSV)
	{
		throw new Error('bad arg: text');
	}

	const result = await dialog.showSaveDialog(win, {
		title: 'Export Recipe',
		defaultPath: csvFileName(defaultName),
		filters: [{name: 'CSV files', extensions: ['csv']}]
	});

	if (result.canceled || !result.filePath)
	{
		return null;
	}

	const file = /\.csv$/i.test(result.filePath) ? result.filePath : result.filePath + '.csv';
	await fs.promises.writeFile(file, text, 'utf8');

	return file;
}

// RecipeImport: asks which file, then reads it. Resolves with {file, text},
// or null when the operator cancels.
export async function importCsv(dialog, win)
{
	const result = await dialog.showOpenDialog(win, {
		title: 'Import Recipe',
		filters: [{name: 'CSV files', extensions: ['csv']}, {name: 'All files', extensions: ['*']}],
		properties: ['openFile']
	});

	if (result.canceled || result.filePaths.length === 0)
	{
		return null;
	}

	const file = result.filePaths[0];
	const stat = await fs.promises.stat(file);

	if (!stat.isFile() || stat.size > MAX_CSV)
	{
		throw new Error('the file is not a recipe (too large)');
	}

	return {file: file, text: await fs.promises.readFile(file, 'utf8')};
}
