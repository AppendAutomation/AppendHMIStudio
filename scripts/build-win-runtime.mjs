// Builds the Windows x64 runtime template HMI > Publish packs into installers:
// the unpacked Windows app with hmi-comms.exe in resources/comms.
//
//   node scripts/build-win-runtime.mjs
//
// Output: dist-win-runtime/win-unpacked (git-ignored), shipped by the Linux
// and macOS editors as resources/runtime/win-x64. A Windows editor uses its
// own install directory instead.
//
// This works on any host without wine: a --dir build needs no NSIS, and
// electron-builder edits the exe's icon and version resource in plain JS.
// DRAWIO_UNSIGNED skips signing, so the template is unsigned; Publish
// renames and rebrands the exe per product.

import {spawnSync} from 'child_process';
import {fileURLToPath} from 'url';
import fs from 'fs';
import path from 'path';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const out = path.join(root, 'dist-win-runtime');

function run(cmd, args, env)
{
	const r = spawnSync(cmd, args, {cwd: root, stdio: 'inherit', shell: process.platform == 'win32',
		env: Object.assign({}, process.env, env)});

	if (r.status !== 0)
	{
		process.exit(r.status ?? 1);
	}
}

run(process.execPath, [path.join('comms', 'scripts', 'publish.mjs'), '--rid', 'win-x64']);

fs.rmSync(out, {recursive: true, force: true});

run('npx', ['electron-builder', '--config', 'electron-builder-win.json', '--win', '--dir', '--x64',
	'--publish', 'never', '-c.directories.output=dist-win-runtime'],
	{DRAWIO_UNSIGNED: 'true'});

const exe = path.join(out, 'win-unpacked', 'draw.io.exe');
const comms = path.join(out, 'win-unpacked', 'resources', 'comms', 'hmi-comms.exe');

for (const f of [exe, comms])
{
	if (!fs.existsSync(f))
	{
		console.error('Missing from the runtime template: ' + path.relative(root, f));
		process.exit(1);
	}
}

console.log('Windows runtime template -> ' + path.relative(root, path.dirname(exe)));
