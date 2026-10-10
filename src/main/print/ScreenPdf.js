// ScreenToPDF(): the main-process side, shared with AppendHMIDesktop. The
// renderer asks for a capture of the screen's rectangle, builds the PDF
// itself (js/hmi/HmiScreenPdf.js) and hands it back here to be saved where
// the operator picks in the OS dialog. Nothing is written anywhere else.
import fs from 'fs';
import path from 'path';

// Far above a one-page screen capture
export const MAX_PDF = 64 * 1024 * 1024;

// A file name for the dialog: no characters a file system refuses, no
// trailing dots or spaces, no reserved device name; .pdf added.
export function pdfFileName(name)
{
	let s = String(name || '').replace(/[\x00-\x1f\x7f<>:"/\\|?*]/g, '_').replace(/[. ]+$/, '').trim();

	if (/^[_. ]*$/.test(s))
	{
		s = 'Screen';
	}

	if (/^(con|prn|aux|nul|com[0-9]|lpt[0-9])$/i.test(s.replace(/\..*$/, '')))
	{
		s += '_';
	}

	return s.slice(0, 120) + '.pdf';
}

function checkRect(rect)
{
	const ok = rect != null && ['x', 'y', 'width', 'height'].every((k) =>
		Number.isFinite(rect[k]) && rect[k] >= 0 && rect[k] <= 100000);

	if (!ok || rect.width < 1 || rect.height < 1)
	{
		throw new Error('bad arg: rect');
	}
}

// The rectangle of the sender's page, in CSS pixels, as a PNG data URL.
export async function capture(contents, rect)
{
	checkRect(rect);
	const zoom = contents.getZoomFactor ? contents.getZoomFactor() : 1;
	const r = {x: Math.round(rect.x * zoom), y: Math.round(rect.y * zoom),
		width: Math.round(rect.width * zoom), height: Math.round(rect.height * zoom)};
	const image = await contents.capturePage(r);

	if (image.isEmpty())
	{
		throw new Error('the window could not be captured');
	}

	return image.toDataURL();
}

// Writes a hidden temporary file beside the target, then renames it into
// place: a file manager watching the folder never sees the file empty or
// half written (Nautilus took an empty one for text and kept that view).
export async function writeWhole(file, bytes)
{
	const tmp = path.join(path.dirname(file), '.' + path.basename(file) + '.' + process.pid + '.tmp');

	try
	{
		await fs.promises.writeFile(tmp, bytes);
		await fs.promises.rename(tmp, file);
	}
	catch (e)
	{
		await fs.promises.rm(tmp, {force: true});

		throw e;
	}
}

// Asks where, then writes. Resolves with the file, or null when the operator
// cancels. dialog is Electron's, win the parent window, data the PDF in base64.
export async function save(dialog, win, defaultName, data)
{
	if (typeof data !== 'string' || data.length > MAX_PDF * 4 / 3 + 4)
	{
		throw new Error('bad arg: data');
	}

	const bytes = Buffer.from(data, 'base64');

	if (bytes.length < 8 || bytes.subarray(0, 5).toString('latin1') !== '%PDF-')
	{
		throw new Error('bad arg: data');
	}

	const result = await dialog.showSaveDialog(win, {
		title: 'Save Screen as PDF',
		defaultPath: pdfFileName(defaultName),
		filters: [{name: 'PDF files', extensions: ['pdf']}]
	});

	if (result.canceled || !result.filePath)
	{
		return null;
	}

	const file = /\.pdf$/i.test(result.filePath) ? result.filePath : result.filePath + '.pdf';
	await writeWhole(file, bytes);

	return file;
}
