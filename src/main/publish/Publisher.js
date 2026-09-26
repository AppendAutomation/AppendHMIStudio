// HMI > Publish: builds a Windows installer for the open HMI project on any
// host, without wine. It pairs a prebuilt Windows app (the "template") with
// resources/hmi-runtime/{runtime.json, project.drawio-hmi}, which switches
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
import {buildNsisScript, parseFileBytes, windowsFileName, SKIP_ROOT, SKIP_RESOURCES} from './NsisScript.js';

export const SCOPES = ['user', 'machine'];
export const COMPRESSIONS = ['fast', 'small'];
const MAX_PROJECT_CHARS = 64 * 1024 * 1024;

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
		exe = 'draw.io.exe';
	}
	else
	{
		dir = path.join(appPath, 'dist-win-runtime', 'win-unpacked');
		exe = 'draw.io.exe';
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
		project: 'project.drawio-hmi',
		windowMode: opts.runtime.windowMode,
		width: opts.width,
		height: opts.height,
		exit: {mode: opts.runtime.exit, salt: opts.runtime.salt, hash: opts.runtime.hash}
	}, null, '\t');
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

	// Resolves with the installer's path
	async build(projectXml, options, outputDir)
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
			await fs.promises.writeFile(path.join(runtimeDir, 'project.drawio-hmi'), projectXml);

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
				exeName: windowsFileName(opts.productName) + '.exe',
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

			await this.compile(nsis, nsi, stage, total);

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

	compile(nsis, nsi, cwd, total)
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

				// Reading the files is most of the work; the last stretch is
				// writing the installer
				const percent = Math.min(95, Math.floor(done / Math.max(1, total) * 95));

				if (percent !== lastPercent)
				{
					lastPercent = percent;
					this.progress('compressing', percent, 'Compressing');
				}
			};

			child.stdout.on('data', onData);
			child.stderr.on('data', onData);

			child.on('error', (e) =>
			{
				reject(new Error('Cannot run NSIS (' + nsis.makensis + '): ' + e.message));
			});

			child.on('close', (code, signal) =>
			{
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

