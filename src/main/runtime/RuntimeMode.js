// Run-only mode for published HMI packages. A package installs this same app
// with resources/hmi-runtime/{runtime.json, project.drawio-hmi}; when that
// folder exists (or --hmi-runtime <dir> names one) the app opens the project
// straight into Run and nothing else: no editor chrome, no updater, no files.
//
// runtime.json (written by HMI > Publish):
//   productName  display name, also the userData folder name
//   version      package version
//   project      project file name inside the runtime folder
//   windowMode   'kiosk' (default), 'fullscreen' or 'window'
//   width/height project resolution, used by the 'window' mode
//   exit         {mode: 'shortcut' (default) | 'password' | 'never', salt, hash}
//                hash = hex SHA-256 of (salt + password)

import crypto from 'crypto';
import fs from 'fs';
import path from 'path';
import { parseDrawioArgs } from '../args.js';

export const RUNTIME_FOLDER = 'hmi-runtime';
export const RUNTIME_CONFIG = 'runtime.json';

export const WINDOW_MODES = ['kiosk', 'fullscreen', 'window'];
export const EXIT_MODES = ['shortcut', 'password', 'never'];

const MAX_PROJECT_BYTES = 64 * 1024 * 1024;

// The runtime folder, or null for the normal editor. argv is process.argv;
// defaultApp is process.defaultApp (true when started as `electron .`, where
// user arguments begin one index later).
export function findRuntimeDir({argv, defaultApp, isPackaged, resourcesPath})
{
	const tokens = defaultApp ? argv.slice() : [null].concat(argv);
	const dir = parseDrawioArgs(tokens).opts.hmiRuntime;

	if (typeof dir === 'string' && dir)
	{
		return path.resolve(dir);
	}

	if (isPackaged && resourcesPath)
	{
		const packaged = path.join(resourcesPath, RUNTIME_FOLDER);

		if (fs.existsSync(path.join(packaged, RUNTIME_CONFIG)))
		{
			return packaged;
		}
	}

	return null;
}

// Reads and normalises runtime.json. Throws with a readable reason when the
// folder is unusable, so the caller can show it instead of a blank window.
export function loadRuntimeConfig(dir)
{
	const file = path.join(dir, RUNTIME_CONFIG);
	let raw;

	try
	{
		raw = JSON.parse(fs.readFileSync(file, 'utf8'));
	}
	catch (e)
	{
		throw new Error('Cannot load ' + file + ': ' + e.message);
	}

	if (raw == null || typeof raw !== 'object')
	{
		throw new Error(file + ' is not a JSON object');
	}

	const project = typeof raw.project === 'string' && raw.project ? raw.project : 'project.drawio-hmi';

	// The project must sit in the runtime folder itself
	if (path.basename(project) !== project)
	{
		throw new Error('Invalid project file name: ' + project);
	}

	const exit = raw.exit != null && typeof raw.exit === 'object' ? raw.exit : {};
	const exitMode = EXIT_MODES.includes(exit.mode) ? exit.mode : 'shortcut';

	if (exitMode === 'password' && (typeof exit.salt !== 'string' || !/^[0-9a-f]{64}$/i.test(exit.hash || '')))
	{
		throw new Error('The exit password is missing from ' + file);
	}

	return {
		dir: dir,
		productName: cleanName(raw.productName) || 'HMI',
		version: typeof raw.version === 'string' ? raw.version : '',
		projectPath: path.join(dir, project),
		windowMode: WINDOW_MODES.includes(raw.windowMode) ? raw.windowMode : 'kiosk',
		width: dimension(raw.width, 1024),
		height: dimension(raw.height, 768),
		exit: {mode: exitMode, salt: exit.salt, hash: exitMode === 'password' ? exit.hash.toLowerCase() : null}
	};
}

function cleanName(name)
{
	return typeof name === 'string' ? name.replace(/[\x00-\x1f<>:"/\\|?*]/g, '').trim().slice(0, 100) : '';
}

function dimension(v, def)
{
	v = Number(v);

	return Number.isInteger(v) && v >= 320 && v <= 16384 ? v : def;
}

// A userData folder of its own, so a runtime never shares settings, storage
// or the single-instance lock with an editor (or another runtime) on the PC
export function runtimeUserDataDir(appData, config)
{
	let name = config.productName.replace(/\.+$/, '');

	if (!name || /^draw\.?io$/i.test(name))
	{
		name = (name || 'HMI') + ' Runtime';
	}

	return path.join(appData, name);
}

// BrowserWindow options for the configured window mode
export function runtimeWindowOptions(config)
{
	const common = {
		title: config.productName,
		backgroundColor: '#000000',
		autoHideMenuBar: true,
		show: false
	};

	switch (config.windowMode)
	{
		case 'window':
			return Object.assign(common, {
				width: config.width,
				height: config.height,
				useContentSize: true,
				resizable: false,
				maximizable: false,
				fullscreenable: false,
				center: true
			});
		case 'fullscreen':
			return Object.assign(common, {fullscreen: true});
		default:
			return Object.assign(common, {kiosk: true, frame: false});
	}
}

export function hashExitPassword(salt, password)
{
	return crypto.createHash('sha256').update(salt + password, 'utf8').digest('hex');
}

// Whether the operator may close the runtime. The renderer asks after the
// exit shortcut (and the password prompt, for 'password').
export function mayExit(config, password)
{
	switch (config.exit.mode)
	{
		case 'shortcut':
			return true;
		case 'password':
		{
			if (typeof password !== 'string')
			{
				return false;
			}

			const given = Buffer.from(hashExitPassword(config.exit.salt, password), 'hex');
			const want = Buffer.from(config.exit.hash, 'hex');

			return given.length === want.length && crypto.timingSafeEqual(given, want);
		}
		default:
			return false;
	}
}

// The project, read here rather than through the renderer's file IPC, which
// refuses anything inside the install directory
export async function readRuntimeProject(config)
{
	const stat = await fs.promises.stat(config.projectPath);

	if (!stat.isFile() || stat.size > MAX_PROJECT_BYTES)
	{
		throw new Error('Not a usable project file: ' + config.projectPath);
	}

	return fs.promises.readFile(config.projectPath, 'utf8');
}

// What the renderer is told about the package (never the exit hash)
export function publicRuntimeInfo(config)
{
	return {
		productName: config.productName,
		version: config.version,
		title: path.basename(config.projectPath),
		windowMode: config.windowMode,
		exitMode: config.exit.mode
	};
}
