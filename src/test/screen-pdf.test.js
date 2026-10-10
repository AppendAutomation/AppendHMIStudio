// ScreenToPDF's main-process side — exercises src/main/print/ScreenPdf.js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'fs';
import os from 'os';
import path from 'path';
import { pdfFileName, capture, save, writeWhole } from '../main/print/ScreenPdf.js';

const PDF = Buffer.from('%PDF-1.4\n%%EOF\n').toString('base64');

function fakeDialog(filePath, seen)
{
	return {showSaveDialog: async (win, options) =>
	{
		seen.push(options);

		return (filePath == null) ? {canceled: true} : {canceled: false, filePath: filePath};
	}};
}

test('pdfFileName keeps good names and scrubs bad ones', () =>
{
	assert.equal(pdfFileName('Main Screen'), 'Main Screen.pdf');
	assert.equal(pdfFileName('Tank 1/2: Level?'), 'Tank 1_2_ Level_.pdf');
	assert.equal(pdfFileName('Overview. '), 'Overview.pdf');
	assert.equal(pdfFileName('CON'), 'CON_.pdf');
	assert.equal(pdfFileName('???'), 'Screen.pdf');
	assert.equal(pdfFileName(''), 'Screen.pdf');
	assert.equal(pdfFileName('a'.repeat(300)).length, 124);
});

test('save writes the PDF where the dialog says, adding .pdf', async () =>
{
	const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'screenpdf-'));
	const seen = [];

	try
	{
		const file = await save(fakeDialog(path.join(dir, 'out'), seen), null, 'Tank: 1', PDF);
		assert.equal(file, path.join(dir, 'out.pdf'));
		assert.equal(fs.readFileSync(file, 'latin1').slice(0, 5), '%PDF-');
		assert.equal(seen[0].defaultPath, 'Tank_ 1.pdf');
		assert.deepEqual(seen[0].filters, [{name: 'PDF files', extensions: ['pdf']}]);
		assert.equal(await save(fakeDialog(path.join(dir, 'kept.PDF'), seen), null, 'x', PDF), path.join(dir, 'kept.PDF'));

		// Replaced whole, and no temporary file left behind
		fs.writeFileSync(path.join(dir, 'out.pdf'), 'old');
		await save(fakeDialog(path.join(dir, 'out.pdf'), seen), null, 'x', PDF);
		assert.equal(fs.readFileSync(path.join(dir, 'out.pdf'), 'latin1').slice(0, 5), '%PDF-');
		assert.deepEqual(fs.readdirSync(dir).sort(), ['kept.PDF', 'out.pdf']);
	}
	finally
	{
		fs.rmSync(dir, {recursive: true, force: true});
	}
});

test('save resolves null on cancel and refuses what is not a PDF', async () =>
{
	assert.equal(await save(fakeDialog(null, []), null, 'x', PDF), null);
	await assert.rejects(save(fakeDialog('/tmp/x', []), null, 'x', Buffer.from('hello').toString('base64')), /bad arg: data/);
	await assert.rejects(save(fakeDialog('/tmp/x', []), null, 'x', 42), /bad arg: data/);
});

test('writeWhole removes its temporary file when the write fails', async () =>
{
	const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'screenpdf-'));

	try
	{
		// The target is a folder, so the rename fails
		fs.mkdirSync(path.join(dir, 'busy.pdf'));
		fs.writeFileSync(path.join(dir, 'busy.pdf', 'x'), 'x');
		await assert.rejects(writeWhole(path.join(dir, 'busy.pdf'), Buffer.from('%PDF-1.4')));
		assert.deepEqual(fs.readdirSync(dir), ['busy.pdf']);
	}
	finally
	{
		fs.rmSync(dir, {recursive: true, force: true});
	}
});

test('capture checks the rectangle and scales it by the zoom', async () =>
{
	let asked = null;
	const contents = {
		getZoomFactor: () => 1.5,
		capturePage: async (r) => { asked = r; return {isEmpty: () => false, toDataURL: () => 'data:image/png;base64,AA'}; }
	};

	assert.equal(await capture(contents, {x: 10, y: 20, width: 100, height: 50}), 'data:image/png;base64,AA');
	assert.deepEqual(asked, {x: 15, y: 30, width: 150, height: 75});
	await assert.rejects(capture(contents, {x: 0, y: 0, width: 0, height: 10}), /bad arg: rect/);
	await assert.rejects(capture(contents, {x: 'a', y: 0, width: 5, height: 10}), /bad arg: rect/);
	await assert.rejects(capture(contents, null), /bad arg: rect/);
});
