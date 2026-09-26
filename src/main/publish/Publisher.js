// HMI > Publish: builds a Windows installer for the open HMI project on any
// host, without wine. It pairs a prebuilt Windows app (the "template") with
// resources/hmi-runtime/{runtime.json, project.ahmi}, which switches
// that app into run-only mode (see ../runtime/RuntimeMode.js), and compiles
// the lot with the bundled NSIS.
//
// Where things come from:
//   template  a packaged Windows editor's own install folder; a packaged
//             Linux/macOS editor's resources/runtime/win-x64; a source
//             checkout's dist-win-runtime/win-unpacked (npm run build-win-runtime)
//   NSIS      resources/nsis (+ bin/makensis) when packaged; build/nsis in a
//             source checkout (npm run fetch-nsis)

import {EventEmitter} from 'events';
import {spawn} from 'child_process';
import fs from 'fs';
import os from 'os';
import path from 'path';
import * as resedit from 'resedit';
import {WINDOWS_EXE} from '../brand.js';
import {buildNsisScript, parseFileBytes, windowsFileName, fourPartVersion, SKIP_ROOT, SKIP_RESOURCES} from './NsisScript.js';

export const SCOPES = ['user', 'machine'];
export const COMPRESSIONS = ['fast', 'small'];
const MAX_PROJECT_CHARS = 64 * 1024 * 1024;

// Solid LZMA shrinks the Windows app to about a third; the installer growing
// towards that is the progress while makensis compresses without a word
const SOLID_RATIO = 0.33;

export function resolveTemplate({platform, isPackaged, execPath, resourcesPath, appPath})
{
	let dir, exe;

	if (isPackaged && platform === 'win32')
	{
		dir = path.dirname(execPath);
		exe = path.basename(execPath);
	}
	else if (isPackaged)
	{
		dir = path.join(resourcesPath, 'runtime', 'win-x64');
		exe = WINDOWS_EXE;
	}
	else
	{
		dir = path.join(appPath, 'dist-win-runtime', 'win-unpacked');
		exe = WINDOWS_EXE;
	}

	const need = [exe, path.join('resources', 'app.asar'), path.join('resources', 'comms', 'hmi-comms.exe')];

	for (const f of need)
	{
		if (!fs.existsSync(path.join(dir, f)))
		{
			throw new Error('The Windows runtime is missing (' + path.join(dir, f) + ').' +
				(isPackaged ? ' Reinstall the application.' : ' Run npm run build-win-runtime first.'));
		}
	}

	return {dir, exe};
}

export function resolveNsis({platform, isPackaged, resourcesPath, appPath})
{
	const bin = platform === 'win32' ? 'makensis.exe' : 'makensis';
	let nsisDir, makensis;

	if (isPackaged)
	{
		nsisDir = path.join(resourcesPath, 'nsis');
		makensis = path.join(nsisDir, 'bin', bin);
	}
	else
	{
		nsisDir = path.join(appPath, 'build', 'nsis', 'share');
		makensis = path.join(appPath, 'build', 'nsis', {win32: 'win', darwin: 'mac'}[platform] || 'linux', bin);
	}

	if (!fs.existsSync(makensis) || !fs.existsSync(path.join(nsisDir, 'Stubs')))
	{
		throw new Error('The NSIS compiler is missing (' + makensis + ').' +
			(isPackaged ? ' Reinstall the application.' : ' Run npm run fetch-nsis first.'));
	}

	return {nsisDir, makensis};
}

// Checks and normalises the options from the Publish dialog
export function normaliseOptions(o)
{
	if (o == null || typeof o !== 'object')
	{
		throw new Error('bad arg: options');
	}

	const productName = typeof o.productName === 'string' ? o.productName.trim() : '';

	if (!windowsFileName(productName))
	{
		throw new Error('Enter a product name.');
	}

	if (productName.length > 100)
	{
		throw new Error('The product name is too long.');
	}

	const version = typeof o.version === 'string' ? o.version.trim() : '';

	if (!/^\d{1,5}(\.\d{1,5}){0,3}$/.test(version))
	{
		throw new Error('The version must be numbers separated by dots, such as 1.0.0.');
	}

	const runtime = o.runtime != null && typeof o.runtime === 'object' ? o.runtime : {};
	const exit = ['shortcut', 'password', 'never'].includes(runtime.exit) ? runtime.exit : 'shortcut';

	if (exit === 'password' && (typeof runtime.salt !== 'string' || !/^[0-9a-f]{64}$/i.test(runtime.hash || '')))
	{
		throw new Error('Set the exit password in Application Settings.');
	}

	return {
		productName: productName,
		version: version,
		publisher: typeof o.publisher === 'string' ? o.publisher.slice(0, 100) : '',
		scope: SCOPES.includes(o.scope) ? o.scope : 'user',
		desktop: o.desktop === true,
		autostart: o.autostart === true,
		compression: COMPRESSIONS.includes(o.compression) ? o.compression : 'small',
		width: Number.isInteger(o.width) ? o.width : 1024,
		height: Number.isInteger(o.height) ? o.height : 768,
		runtime: {
			windowMode: ['kiosk', 'fullscreen', 'window'].includes(runtime.windowMode) ? runtime.windowMode : 'kiosk',
			exit: exit,
			salt: exit === 'password' ? runtime.salt : undefined,
			hash: exit === 'password' ? runtime.hash.toLowerCase() : undefined
		}
	};
}

export function runtimeJson(opts)
{
	return JSON.stringify({
		productName: opts.productName,
		version: opts.version,
		project: 'project.ahmi',
		windowMode: opts.runtime.windowMode,
		width: opts.width,
		height: opts.height,
		exit: {mode: opts.runtime.exit, salt: opts.runtime.salt, hash: opts.runtime.hash}
	}, null, '\t');
}

// Writes a copy of the template exe carrying the product's name, version and
// publisher (what Task Manager, the taskbar and file properties show) and
// optionally its icon. Any signature is dropped: the edit would break it.
export async function brandExecutable(src, dest, {productName, version, publisher, exeName, iconPath})
{
	const exe = resedit.NtExecutable.from(await fs.promises.readFile(src), {ignoreCert: true});
	const res = resedit.NtExecutableResource.from(exe);
	const list = resedit.Resource.VersionInfo.fromEntries(res.entries);
	const vi = list.length > 0 ? list[0] : resedit.Resource.VersionInfo.createEmpty();
	const langs = vi.getAllLanguagesForStringValues();
	const lang = langs.length > 0 ? langs[0] : {lang: 0x0409, codepage: 1200};

	if (list.length === 0)
	{
		vi.lang = lang.lang;
	}

	vi.setFileVersion(fourPartVersion(version));
	vi.setProductVersion(fourPartVersion(version));
	vi.setStringValues(lang, {
		ProductName: productName,
		FileDescription: productName,
		CompanyName: publisher || '',
		LegalCopyright: publisher || '',
		InternalName: exeName,
		OriginalFilename: exeName,
		FileVersion: version,
		ProductVersion: version
	});
	vi.outputToResourceEntries(res.entries);

	if (iconPath != null)
	{
		const icon = resedit.Data.IconFile.from(await fs.promises.readFile(iconPath));
		const group = res.entries.find(e => e.type === 14);

		resedit.Resource.IconGroupEntry.replaceIconsForResource(res.entries, group != null ? group.id : 1,
			group != null ? group.lang : lang.lang, icon.icons.map(i => i.data));
	}

	res.outputResource(exe);
	await fs.promises.writeFile(dest, Buffer.from(exe.generate()));
}

// A usable .ico (Windows shows it for the exe, shortcuts and the window)
export async function checkIcon(iconPath)
{
	try
	{
		const icon = resedit.Data.IconFile.from(await fs.promises.readFile(iconPath));

		if (icon.icons.length === 0)
		{
			throw new Error('no images');
		}
	}
	catch (e)
	{
		throw new Error('Not a usable icon (.ico) file: ' + path.basename(iconPath));
	}
}

export function installerName(opts)
{
	return windowsFileName(opts.productName) + '-' + opts.version + '-Setup.exe';
}

async function entries(dir)
{
	return (await fs.promises.readdir(dir, {withFileTypes: true}))
		.map(d => ({name: d.name, dir: d.isDirectory()}))
		.sort((a, b) => a.name.localeCompare(b.name));
}

async function treeSize(p)
{
	const st = await fs.promises.lstat(p);

	if (!st.isDirectory())
	{
		return st.size;
	}

	let total = 0;

	for (const name of await fs.promises.readdir(p))
	{
		total += await treeSize(path.join(p, name));
	}

	return total;
}

// One build at a time. Emits 'progress' {stage, percent, message}, where stage
// is 'preparing', 'compressing', 'done', 'error' or 'cancelled'.
export class Publisher extends EventEmitter
{
	constructor(env)
	{
		super();
		// {platform, isPackaged, execPath, resourcesPath, appPath, tmpDir}
		this.env = env;
		this.child = null;
		this.cancelled = false;
		this.busy = false;
	}

	available()
	{
		const result = {template: null, nsis: null, error: null};

		try
		{
			result.template = resolveTemplate(this.env).dir;
			result.nsis = resolveNsis(this.env).makensis;
		}
		catch (e)
		{
			result.error = e.message;
		}

		return result;
	}

	progress(stage, percent, message)
	{
		this.emit('progress', {stage, percent, message});
	}

	// Resolves with the installer's path. iconPath is an .ico the caller has
	// authorised, or null.
	async build(projectXml, options, outputDir, iconPath)
	{
		if (this.busy)
		{
			throw new Error('A package is already being built.');
		}

		if (typeof projectXml !== 'string' || !projectXml || projectXml.length > MAX_PROJECT_CHARS)
		{
			throw new Error('bad arg: projectXml');
		}

		const opts = normaliseOptions(options);
		this.busy = true;
		this.cancelled = false;
		let stage = null;

		try
		{
			this.progress('preparing', 0, 'Preparing');
			const template = resolveTemplate(this.env);
			const nsis = resolveNsis(this.env);

			stage = await fs.promises.mkdtemp(path.join(this.env.tmpDir || os.tmpdir(), 'hmi-publish-'));
			const runtimeDir = path.join(stage, 'hmi-runtime');
			await fs.promises.mkdir(runtimeDir);
			await fs.promises.writeFile(path.join(runtimeDir, 'runtime.json'), runtimeJson(opts));
			await fs.promises.writeFile(path.join(runtimeDir, 'project.ahmi'), projectXml);

			if (iconPath != null)
			{
				await checkIcon(iconPath);
				await fs.promises.copyFile(iconPath, path.join(runtimeDir, 'icon.ico'));
			}

			const exeName = windowsFileName(opts.productName) + '.exe';
			const exeSource = path.join(stage, 'app.exe');
			this.progress('preparing', 0, 'Branding');
			await brandExecutable(path.join(template.dir, template.exe), exeSource,
				Object.assign({exeName, iconPath}, opts));

			const root = await entries(template.dir);
			const resources = await entries(path.join(template.dir, 'resources'));

			// What the installer will carry, for the progress bar and the
			// size shown in Add/Remove Programs
			let total = 0;

			for (const e of root)
			{
				if (e.name !== 'resources' && !SKIP_ROOT.some(r => r.test(e.name)))
				{
					total += await treeSize(path.join(template.dir, e.name));
				}
			}

			for (const e of resources)
			{
				if (!SKIP_RESOURCES.includes(e.name))
				{
					total += await treeSize(path.join(template.dir, 'resources', e.name));
				}
			}

			total += await treeSize(runtimeDir);

			const outFile = path.join(stage, installerName(opts));
			const script = buildNsisScript(Object.assign({}, opts, {
				exeName: exeName,
				exeSource: exeSource,
				outFile: outFile,
				template: Object.assign({root, resources}, template),
				runtimeDir: runtimeDir,
				join: path.join,
				estimatedSizeKb: total / 1024
			}));

			const nsi = path.join(stage, 'installer.nsi');
			await fs.promises.writeFile(nsi, script);

			if (this.cancelled)
			{
				throw new Error('cancelled');
			}

			await this.compile(nsis, nsi, stage, total, opts.compression === 'small' ? outFile : null);

			const dest = path.join(outputDir, installerName(opts));
			await fs.promises.copyFile(outFile, dest);
			this.progress('done', 100, dest);

			return dest;
		}
		catch (e)
		{
			if (this.cancelled)
			{
				this.progress('cancelled', 0, 'Cancelled');
				throw new Error('cancelled');
			}

			this.progress('error', 0, e.message);
			throw e;
		}
		finally
		{
			this.busy = false;
			this.child = null;

			if (stage != null)
			{
				await fs.promises.rm(stage, {recursive: true, force: true}).catch(() => {});
			}
		}
	}

	// solidOut: the installer being written by a solid build, whose size is
	// the progress once the files have been listed
	compile(nsis, nsi, cwd, total, solidOut)
	{
		return new Promise((resolve, reject) =>
		{
			const child = spawn(nsis.makensis, ['-V4', '-INPUTCHARSET', 'UTF8', nsi], {
				cwd: cwd,
				env: Object.assign({}, process.env, {NSISDIR: nsis.nsisDir}),
				windowsHide: true
			});

			this.child = child;

			let done = 0;
			let lastPercent = -1;
			let partial = '';
			const tail = [];

			const report = (percent) =>
			{
				percent = Math.min(99, Math.floor(percent));

				if (percent > lastPercent)
				{
					lastPercent = percent;
					this.progress('compressing', percent, 'Compressing');
				}
			};

			const poll = (solidOut == null) ? null : setInterval(() =>
			{
				fs.promises.stat(solidOut).then((st) =>
				{
					report(5 + 94 * Math.min(1, st.size / Math.max(1, total * SOLID_RATIO)));
				}, () => {});
			}, 1000);

			const onData = (chunk) =>
			{
				const lines = (partial + chunk.toString('utf8')).split(/\r?\n/);
				partial = lines.pop();

				for (const line of lines)
				{
					done += parseFileBytes(line);
					tail.push(line);

					if (tail.length > 30)
					{
						tail.shift();
					}
				}

				// Without solid compression each file is compressed as it is
				// listed, so the files are the progress; a solid build only
				// reads them here and compresses afterwards
				report(done / Math.max(1, total) * ((solidOut == null) ? 95 : 5));
			};

			child.stdout.on('data', onData);
			child.stderr.on('data', onData);

			child.on('error', (e) =>
			{
				if (poll != null)
				{
					clearInterval(poll);
				}

				reject(new Error('Cannot run NSIS (' + nsis.makensis + '): ' + e.message));
			});

			child.on('close', (code, signal) =>
			{
				if (poll != null)
				{
					clearInterval(poll);
				}

				if (code === 0)
				{
					resolve();
				}
				else
				{
					const reason = tail.filter(l => /error|warning|!/i.test(l)).slice(-5).join('\n') ||
						tail.slice(-5).join('\n');
					reject(new Error('NSIS failed (' + (signal || code) + '): ' + reason));
				}
			});
		});
	}

	cancel()
	{
		if (!this.busy)
		{
			return false;
		}

		this.cancelled = true;

		if (this.child != null)
		{
			this.child.kill();
		}

		return true;
	}
}

