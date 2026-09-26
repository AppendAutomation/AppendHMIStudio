// Fetches the NSIS compiler that HMI > Publish runs to build a Windows
// installer, and lays out the part of it the editor ships.
//
//   node scripts/fetch-nsis.mjs
//
// It is the same nsis-3.0.4.1 bundle electron-builder uses for its own NSIS
// targets, downloaded and checksum-verified through electron-builder's cache.
// Output (git-ignored), picked up as extraResources by the electron-builder
// configs:
//
//   build/nsis/share/   Stubs, Include, Plugins and nsisconf.nsh (NSISDIR)
//   build/nsis/linux/   makensis for Linux x64
//   build/nsis/mac/     makensis for macOS (x64, runs under Rosetta on arm64)
//   build/nsis/win/     makensis.exe and zlib1.dll
//
// Packaged, share/ becomes resources/nsis/ and the host binary goes to
// resources/nsis/bin/. NSISDIR is always passed explicitly when makensis runs.

import {createRequire} from 'module';
import {fileURLToPath} from 'url';
import fs from 'fs';
import path from 'path';

const require = createRequire(import.meta.url);
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const out = path.join(root, 'build', 'nsis');

const RELEASE = 'nsis-3.0.4.1';
const ARCHIVE = 'nsis-3.0.4.1.7z';
const SHA256 = '9877df902530f96357d13a7a31ae2b9df67f48b11ffc9a1700a7c961574ec5fa';

// All stubs (makensis loads the ANSI zlib stub while initialising) but only the
// Unicode plugins: generated scripts always say "Unicode true"
const SHARE = [
	'COPYING',
	'nsisconf.nsh',
	'Include',
	'Stubs',
	path.join('Plugins', 'x86-unicode')
];

const BINARIES = {
	linux: [path.join('linux', 'makensis')],
	mac: [path.join('mac', 'makensis')],
	win: [path.join('Bin', 'makensis.exe'), path.join('Bin', 'zlib1.dll')]
};

function copy(src, dest)
{
	fs.mkdirSync(path.dirname(dest), {recursive: true});
	fs.cpSync(src, dest, {recursive: true, preserveTimestamps: true});
}

const {getBinFromUrl} = require('app-builder-lib/out/binDownload');
const bundle = await getBinFromUrl(RELEASE, ARCHIVE, SHA256);

fs.rmSync(out, {recursive: true, force: true});

for (const rel of SHARE)
{
	copy(path.join(bundle, rel), path.join(out, 'share', rel));
}

for (const [platform, files] of Object.entries(BINARIES))
{
	for (const rel of files)
	{
		const dest = path.join(out, platform, path.basename(rel));
		copy(path.join(bundle, rel), dest);

		if (platform != 'win')
		{
			fs.chmodSync(dest, 0o755);
		}
	}
}

console.log(`NSIS ${RELEASE} -> ${path.relative(root, out)}`);
