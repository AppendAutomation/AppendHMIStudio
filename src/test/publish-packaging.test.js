// Packaging for HMI > Publish: the electron-builder configs ship the NSIS
// compiler (scripts/fetch-nsis.mjs) and, on Linux and macOS, the Windows runtime
// template (scripts/build-win-runtime.mjs), and keep both out of app.asar
import { test, describe } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'child_process';
import fs from 'fs';
import os from 'os';
import path from 'path';
import { fileURLToPath } from 'url';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const readConfig = (name) => JSON.parse(fs.readFileSync(path.join(root, name), 'utf8'));

const CONFIGS = ['electron-builder-win.json', 'electron-builder-win-arm64.json', 'electron-builder-appx.json',
	'electron-builder-linux-mac.json', 'electron-builder-snap.json'];

function resources(list)
{
	return Object.fromEntries((list || []).map(r => [r.to, r.from]));
}

describe('publish packaging', () =>
{
	test('no config packs the template or the build scripts into app.asar', () =>
	{
		for (const name of CONFIGS)
		{
			const files = readConfig(name).files;
			assert.ok(files.includes('!dist-win-runtime{,/**}'), name);
			assert.ok(files.includes('!scripts{,/**}'), name);
		}
	});

	test('Windows editors ship NSIS and use their own install as the template', () =>
	{
		for (const name of ['electron-builder-win.json', 'electron-builder-win-arm64.json', 'electron-builder-appx.json'])
		{
			const res = resources(readConfig(name).extraResources);
			assert.equal(res['nsis'], 'build/nsis/share', name);
			assert.equal(res['nsis/bin'], 'build/nsis/win', name);
			assert.equal(res['runtime/win-x64'], undefined, name);
		}
	});

	test('Linux and macOS editors ship NSIS for their platform and the Windows template', () =>
	{
		const config = readConfig('electron-builder-linux-mac.json');

		for (const platform of ['linux', 'mac'])
		{
			const res = resources(config[platform].extraResources);
			assert.equal(res['runtime/win-x64'], 'dist-win-runtime/win-unpacked', platform);
			assert.equal(res['nsis'], 'build/nsis/share', platform);
			assert.equal(res['nsis/bin'], 'build/nsis/' + platform, platform);
		}

		// The Windows tree holds no Mach-O binaries, codesign must leave it alone
		assert.ok(config.mac.signIgnore.some(p => p.includes('Resources/runtime/')));
	});

	const makensis = path.join(root, 'build', 'nsis', {linux: 'linux', darwin: 'mac', win32: 'win'}[process.platform] || '',
		process.platform == 'win32' ? 'makensis.exe' : 'makensis');

	test('the fetched NSIS compiles an installer', {skip: !fs.existsSync(makensis) && 'run npm run fetch-nsis'}, () =>
	{
		const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'nsis-test-'));

		try
		{
			fs.mkdirSync(path.join(dir, 'app'));
			fs.writeFileSync(path.join(dir, 'app', 'a.txt'), 'a');
			fs.writeFileSync(path.join(dir, 't.nsi'), [
				'Unicode true',
				'Name "T"',
				'OutFile "T-Setup.exe"',
				'RequestExecutionLevel user',
				'InstallDir "$LOCALAPPDATA\\Programs\\T"',
				'SetCompressor /SOLID lzma',
				'Page directory',
				'Page instfiles',
				'Section "Install"',
				'  SetOutPath "$INSTDIR"',
				'  File /r "app\\*.*"',
				'  WriteUninstaller "$INSTDIR\\Uninstall.exe"',
				'SectionEnd',
				'Section "Uninstall"',
				'  RMDir /r "$INSTDIR"',
				'SectionEnd',
				''].join('\n'));

			const r = spawnSync(makensis, ['-V2', 't.nsi'], {cwd: dir, encoding: 'utf8',
				env: Object.assign({}, process.env, {NSISDIR: path.join(root, 'build', 'nsis', 'share')})});

			assert.equal(r.status, 0, r.stdout + r.stderr);
			assert.equal(fs.readFileSync(path.join(dir, 'T-Setup.exe')).subarray(0, 2).toString(), 'MZ');
		}
		finally
		{
			fs.rmSync(dir, {recursive: true, force: true});
		}
	});
});
