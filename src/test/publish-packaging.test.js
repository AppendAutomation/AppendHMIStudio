// Packaging for HMI > Publish: the electron-builder configs ship the NSIS
// compiler (scripts/fetch-nsis.mjs) and, on Linux, the Windows runtime
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

const CONFIGS = ['electron-builder-win.json', 'electron-builder-linux.json'];

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
			// electron-builder skips only its own output folder: the other
			// build's packages in dist/ would otherwise land in app.asar
			assert.ok(files.includes('!dist{,/**}'), name);
			assert.ok(files.includes('!scripts{,/**}'), name);
		}
	});

	test('the SQL Server driver ships without its Azure AD half', () =>
	{
		for (const name of CONFIGS)
		{
			const files = readConfig(name).files;
			assert.ok(files.includes(
				'!node_modules/@azure/{identity,keyvault-keys,msal-browser,msal-node,msal-common}{,/**}'),
				name + ' packs the Azure AD packages, which are about 43 MB of code that cannot run here');
			assert.ok(files.includes('!node_modules/@types{,/**}'), name);

			// The driver requires both eagerly, so they cannot simply be left
			// out: stubs take their place (build/sql-stubs/README.md)
			const mapped = Object.fromEntries(files.filter((f) => typeof f === 'object')
				.map((f) => [f.to, f.from]));
			assert.equal(mapped['node_modules/@azure/identity'], 'build/sql-stubs/azure-identity', name);
			assert.equal(mapped['node_modules/@azure/keyvault-keys'],
				'build/sql-stubs/azure-keyvault-keys', name);
		}
	});

	test('every glob comes before the first file set, or the ones after it stop applying', () =>
	{
		// electron-builder reads a pattern that follows a {from, to} entry as
		// part of that entry's file set. With the exclusions after the stub
		// mappings, dist/ and the previous unpacked build went into app.asar
		// and it came out at 10 GB.
		for (const name of CONFIGS)
		{
			const files = readConfig(name).files;
			const firstObject = files.findIndex((f) => typeof f === 'object');

			if (firstObject >= 0)
			{
				assert.ok(files.slice(firstObject).every((f) => typeof f === 'object'),
					name + ' has a glob after a file set entry, so the exclusions after it are ignored');
			}
		}
	});

	test('the stub packages are present and export what the driver looks up', () =>
	{
		for (const stub of ['azure-identity', 'azure-keyvault-keys'])
		{
			const dir = path.join(root, 'build', 'sql-stubs', stub);
			assert.ok(fs.existsSync(path.join(dir, 'package.json')), stub);
			assert.ok(fs.existsSync(path.join(dir, 'index.js')), stub);
		}

		const identity = fs.readFileSync(path.join(root, 'build', 'sql-stubs',
			'azure-identity', 'index.js'), 'utf8');

		for (const name of ['ClientSecretCredential', 'DefaultAzureCredential',
			'ManagedIdentityCredential', 'UsernamePasswordCredential'])
		{
			assert.ok(identity.includes(name), 'the identity stub is missing ' + name);
		}

		const vault = fs.readFileSync(path.join(root, 'build', 'sql-stubs',
			'azure-keyvault-keys', 'index.js'), 'utf8');
		assert.ok(vault.includes('KeyClient') && vault.includes('CryptographyClient'));
	});

	test('the Windows editor ships NSIS and uses its own install as the template', () =>
	{
		const res = resources(readConfig('electron-builder-win.json').extraResources);
		assert.equal(res['nsis'], 'build/nsis/share');
		assert.equal(res['nsis/bin'], 'build/nsis/win');
		assert.equal(res['runtime/win-x64'], undefined);
	});

	test('the Linux editor ships NSIS for Linux and the Windows template', () =>
	{
		const res = resources(readConfig('electron-builder-linux.json').extraResources);
		assert.equal(res['runtime/win-x64'], 'dist-win-runtime/win-unpacked');
		assert.equal(res['nsis'], 'build/nsis/share');
		assert.equal(res['nsis/bin'], 'build/nsis/linux');
	});

	test('both ship the product icon for windows and installers', () =>
	{
		for (const name of CONFIGS)
		{
			const res = resources(readConfig(name).extraResources);
			assert.equal(res['icon.png'], 'build/icon.png', name);
			assert.equal(res['icon.ico'], 'build/icon.ico', name);
		}
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
