// Builds the Append HMI Studio Linux packages (AppImage and deb, x64):
// NSIS and the Windows runtime template for HMI > Publish, the comms server,
// then electron-builder.
//
//   node scripts/dist-linux.mjs [--skip-runtime]
//
// Output in dist/. --skip-runtime reuses an existing dist-win-runtime.

import {spawnSync} from 'child_process';
import {fileURLToPath} from 'url';
import path from 'path';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);

function run(cmd, cmdArgs)
{
	const r = spawnSync(cmd, cmdArgs, {cwd: root, stdio: 'inherit', shell: process.platform == 'win32'});

	if (r.status !== 0)
	{
		process.exit(r.status ?? 1);
	}
}

run(process.execPath, [path.join('scripts', 'fetch-nsis.mjs')]);
run(process.execPath, [path.join('comms', 'scripts', 'publish.mjs'), '--rid', 'linux-x64']);

if (args.indexOf('--skip-runtime') < 0)
{
	run(process.execPath, [path.join('scripts', 'build-win-runtime.mjs')]);
}

run('npx', ['electron-builder', '--config', 'electron-builder-linux.json', '--linux', '--x64', '--publish', 'never']);
