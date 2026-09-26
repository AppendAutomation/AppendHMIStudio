// Builds the Append HMI Studio installer for Windows x64 on any host,
// without wine: the unpacked app from scripts/build-win-runtime.mjs (the same
// tree HMI > Publish uses as its template) compiled into an NSIS installer
// with the bundled makensis.
//
//   node scripts/dist-win.mjs [--scope machine|user] [--compression small|fast]
//                             [--skip-build]
//
// Output: dist/Append-HMI-Studio-<version>-Setup.exe. Per-machine (Program
// Files, administrator) by default. --skip-build reuses dist-win-runtime.

import {spawnSync} from 'child_process';
import {fileURLToPath} from 'url';
import fs from 'fs';
import os from 'os';
import path from 'path';
import {buildNsisScript, SKIP_ROOT, SKIP_EDITOR_RESOURCES} from '../src/main/publish/NsisScript.js';
import {PRODUCT_NAME, PUBLISHER, WINDOWS_EXE} from '../src/main/brand.js';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const arg = (name, def) => (args.indexOf(name) >= 0) ? args[args.indexOf(name) + 1] : def;

const scope = arg('--scope', 'machine');
const compression = arg('--compression', 'small');

if (!['machine', 'user'].includes(scope) || !['small', 'fast'].includes(compression))
{
	console.error('Usage: node scripts/dist-win.mjs [--scope machine|user] [--compression small|fast] [--skip-build]');
	process.exit(2);
}

function run(cmd, cmdArgs, env)
{
	const r = spawnSync(cmd, cmdArgs, {cwd: root, stdio: 'inherit', shell: process.platform == 'win32',
		env: Object.assign({}, process.env, env)});

	if (r.status !== 0)
	{
		process.exit(r.status ?? 1);
	}
}

const nsisDir = path.join(root, 'build', 'nsis', 'share');
const makensis = path.join(root, 'build', 'nsis', {win32: 'win', darwin: 'mac'}[process.platform] || 'linux',
	process.platform == 'win32' ? 'makensis.exe' : 'makensis');

if (!fs.existsSync(makensis))
{
	run(process.execPath, [path.join('scripts', 'fetch-nsis.mjs')]);
}

if (args.indexOf('--skip-build') < 0)
{
	run(process.execPath, [path.join('scripts', 'build-win-runtime.mjs')]);
}

const appDir = path.join(root, 'dist-win-runtime', 'win-unpacked');

if (!fs.existsSync(path.join(appDir, WINDOWS_EXE)))
{
	console.error('No Windows build in ' + path.relative(root, appDir) + '; run without --skip-build');
	process.exit(1);
}

const entries = (dir) => fs.readdirSync(dir, {withFileTypes: true})
	.map(d => ({name: d.name, dir: d.isDirectory()}))
	.sort((a, b) => a.name.localeCompare(b.name));

const size = (p) =>
{
	const st = fs.lstatSync(p);

	return st.isDirectory() ? fs.readdirSync(p).reduce((t, n) => t + size(path.join(p, n)), 0) : st.size;
};

const rootEntries = entries(appDir);
const resources = entries(path.join(appDir, 'resources'));
const total = rootEntries.filter(e => e.name !== 'resources' && !SKIP_ROOT.some(r => r.test(e.name)))
	.concat(resources.filter(e => !SKIP_EDITOR_RESOURCES.includes(e.name)).map(e => ({name: path.join('resources', e.name)})))
	.reduce((t, e) => t + size(path.join(appDir, e.name)), 0);

const version = JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8')).version;
const outDir = path.join(root, 'dist');
const outFile = path.join(outDir, 'Append-HMI-Studio-' + version + (scope == 'user' ? '-user' : '') + '-Setup.exe');
fs.mkdirSync(outDir, {recursive: true});

const script = buildNsisScript({
	productName: PRODUCT_NAME,
	version: version,
	publisher: PUBLISHER,
	exeName: WINDOWS_EXE,
	scope: scope,
	desktop: true,
	autostart: false,
	compression: compression,
	outFile: outFile,
	template: {dir: appDir, exe: WINDOWS_EXE, root: rootEntries, resources: resources},
	runtimeDir: null,
	editor: true,
	fileAssociation: {ext: 'ahmi', progId: 'AppendHMIStudio.Project', description: 'HMI Application'},
	installerIcon: path.join(root, 'build', 'icon.ico'),
	join: path.join,
	estimatedSizeKb: total / 1024
});

const stage = fs.mkdtempSync(path.join(os.tmpdir(), 'hmi-dist-win-'));

try
{
	const nsi = path.join(stage, 'installer.nsi');
	fs.writeFileSync(nsi, script);
	console.log('Compiling ' + path.relative(root, outFile) + ' (' + scope + ', ' + compression + ')');
	run(makensis, ['-V2', '-INPUTCHARSET', 'UTF8', nsi], {NSISDIR: nsisDir});
}
finally
{
	fs.rmSync(stage, {recursive: true, force: true});
}

console.log('Windows installer -> ' + path.relative(root, outFile));
