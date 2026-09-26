// Renders the product icon from build/icon.svg (and build/icon-small.svg,
// simplified for 16-32 px) into every file packaging and the app use:
//
//   build/<n>x<n>.png, build/icon.png   Linux packages, window icon
//   build/icon.ico                      Windows exe, installers, shortcuts
//   drawio/.../images/hmi-logo.svg      the editor's logo and About dialog
//
//   node scripts/make-icons.mjs        (needs inkscape and ImageMagick)
//
// The results are committed, so builds don't need either tool.

import {execFileSync} from 'child_process';
import {fileURLToPath} from 'url';
import fs from 'fs';
import os from 'os';
import path from 'path';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const build = path.join(root, 'build');
const SMALL = 32;

function render(size, dest)
{
	const svg = path.join(build, size <= SMALL ? 'icon-small.svg' : 'icon.svg');
	execFileSync('inkscape', [svg, '--export-type=png', '--export-filename=' + dest,
		'-w', String(size), '-h', String(size)], {stdio: ['ignore', 'ignore', 'pipe']});
}

for (const size of [16, 32, 48, 64, 96, 128, 192, 256, 512, 1024])
{
	render(size, path.join(build, size + 'x' + size + '.png'));
}

fs.copyFileSync(path.join(build, '1024x1024.png'), path.join(build, 'icon.png'));

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'hmi-icons-'));

try
{
	const layers = [16, 24, 32, 48, 64, 128, 256].map((size) =>
	{
		const f = path.join(tmp, size + '.png');
		render(size, f);

		return f;
	});

	execFileSync('convert', layers.concat([path.join(build, 'icon.ico')]));
}
finally
{
	fs.rmSync(tmp, {recursive: true, force: true});
}

fs.copyFileSync(path.join(build, 'icon.svg'),
	path.join(root, 'drawio', 'src', 'main', 'webapp', 'images', 'hmi-logo.svg'));

console.log('Icons written to build/ and the editor images');
