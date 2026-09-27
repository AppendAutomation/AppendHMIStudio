// HMI > Publish — exercises src/main/publish/Publisher.js and NsisScript.js,
// including a real makensis build from a small fake template when build/nsis
// is present (npm run fetch-nsis)
import { test, describe, after } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'child_process';
import fs from 'fs';
import os from 'os';
import path from 'path';
import { fileURLToPath } from 'url';
import { buildNsisScript, nsisString, windowsFileName, fourPartVersion, parseFileBytes } from '../main/publish/NsisScript.js';
import { Publisher, resolveTemplate, resolveNsis, normaliseOptions, runtimeJson, installerName, brandExecutable,
	checkIcon, scanTemplate } from '../main/publish/Publisher.js';
import { createRequire } from 'module';
import * as resedit from 'resedit';
import { loadRuntimeConfig, mayExit, hashExitPassword } from '../main/runtime/RuntimeMode.js';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', '..');

// A small real Windows executable with a version resource, from NSIS
const samplePe = path.join(root, 'build', 'nsis', 'win', 'makensis.exe');
const icon = path.join(root, 'build', 'icon.ico');

function versionStrings(file)
{
	const exe = resedit.NtExecutable.from(fs.readFileSync(file), {ignoreCert: true});
	const res = resedit.NtExecutableResource.from(exe);
	const vi = resedit.Resource.VersionInfo.fromEntries(res.entries)[0];

	return {strings: vi.getStringValues(vi.getAllLanguagesForStringValues()[0]),
		icons: res.entries.filter(e => e.type === 3).length};
}

const tempDirs = [];

function tempDir()
{
	const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'hmi-publish-test-'));
	tempDirs.push(dir);

	return dir;
}

after(() =>
{
	for (const dir of tempDirs)
	{
		fs.rmSync(dir, {recursive: true, force: true});
	}
});

// A tiny stand-in for win-unpacked, with the things a package must leave out
function fakeTemplate()
{
	const dir = tempDir();
	const put = (rel, data) =>
	{
		fs.mkdirSync(path.dirname(path.join(dir, rel)), {recursive: true});
		fs.writeFileSync(path.join(dir, rel), data || rel);
	};

	if (fs.existsSync(samplePe))
	{
		put('Append HMI Studio.exe', fs.readFileSync(samplePe));
	}
	else
	{
		put('Append HMI Studio.exe', 'MZ fake exe');
	}

	put('ffmpeg.dll');
	put('Uninstall Append HMI Studio.exe');
	put('locales/en-US.pak');
	put('resources/app.asar');
	put('resources/app-update.yml');
	put('resources/comms/hmi-comms.exe');
	put('resources/nsis/bin/makensis.exe');
	put('resources/runtime/win-x64/Append HMI Studio.exe');

	return dir;
}

const baseOptions = {productName: 'Line 3 HMI', version: '1.2.0', width: 800, height: 480,
	runtime: {windowMode: 'kiosk', exit: 'shortcut'}};

describe('NsisScript helpers', () =>
{
	test('NSIS strings escape $ and quotes', () =>
	{
		assert.equal(nsisString('a$b"c'), 'a$$b$\\"c');
		assert.equal(nsisString('two\nlines'), 'two lines');
	});

	test('product names become Windows file names', () =>
	{
		assert.equal(windowsFileName('Line: 3/4?'), 'Line 34');
		assert.equal(windowsFileName('Trailing. '), 'Trailing');
		assert.equal(windowsFileName('CON'), '_CON');
		assert.equal(windowsFileName('***'), '');
	});

	test('versions pad to four parts', () =>
	{
		assert.equal(fourPartVersion('1.2'), '1.2.0.0');
		assert.equal(fourPartVersion('1.2.3.4'), '1.2.3.4');
	});

	test('file progress lines', () =>
	{
		assert.equal(parseFileBytes('File: "app.asar" 1234 bytes'), 1234);
		assert.equal(parseFileBytes('File: "app.asar" [compress] 1234 bytes'), 1234);
		assert.equal(parseFileBytes('File: "Append HMI Studio.exe"->"$INSTDIR\\P.exe" [compress] 3299/6656 bytes'), 6656);
		assert.equal(parseFileBytes('File: Descending to: "locales"'), 0);
	});
});

describe('buildNsisScript', () =>
{
	const template = {dir: '/t', exe: 'Append HMI Studio.exe',
		root: [{name: 'Append HMI Studio.exe'}, {name: 'ffmpeg.dll'}, {name: 'Uninstall Append HMI Studio.exe'},
			{name: 'locales', dir: true}, {name: 'resources', dir: true}],
		resources: [{name: 'app.asar'}, {name: 'app-update.yml'}, {name: 'comms', dir: true},
			{name: 'nsis', dir: true}, {name: 'runtime', dir: true}]};

	function script(extra)
	{
		return buildNsisScript(Object.assign({}, normaliseOptions(baseOptions), {exeName: 'Line 3 HMI.exe',
			outFile: '/o/Setup.exe', template: template, runtimeDir: '/s/hmi-runtime',
			join: path.posix.join}, extra));
	}

	test('installs the template under the product exe name, minus editor-only parts', () =>
	{
		const s = script();

		assert.match(s, /File "\/oname=\$\{EXE\}" "\/t\/Append HMI Studio\.exe"/);
		assert.match(s, /File "\/t\/ffmpeg\.dll"/);
		assert.match(s, /File \/r "\/t\/locales"/);
		assert.match(s, /File "\/t\/resources\/app\.asar"/);
		assert.match(s, /File \/r "\/t\/resources\/comms"/);
		assert.match(s, /File \/r "\/s\/hmi-runtime\/\*\.\*"/);
		assert.doesNotMatch(s, /Uninstall Append HMI Studio\.exe/);
		assert.doesNotMatch(s, /resources\/nsis|resources\/runtime"|app-update/);
	});

	test('per-user by default: no admin, HKCU, LocalAppData', () =>
	{
		const s = script();

		assert.match(s, /RequestExecutionLevel user/);
		assert.match(s, /InstallDir "\$LOCALAPPDATA\\Programs\\Line 3 HMI"/);
		assert.match(s, /WriteRegStr HKCU/);
		assert.doesNotMatch(s, /HKLM/);
	});

	test('per-machine: admin, HKLM, Program Files', () =>
	{
		const s = script({scope: 'machine'});

		assert.match(s, /RequestExecutionLevel admin/);
		assert.match(s, /InstallDir "\$PROGRAMFILES64\\Line 3 HMI"/);
		assert.match(s, /WriteRegStr HKLM/);
		assert.match(s, /SetShellVarContext all/);
	});

	test('shortcuts follow the options', () =>
	{
		const off = script();
		assert.match(off, /Delete "\$DESKTOP\\\$\{PRODUCT\}\.lnk"/);
		assert.match(off, /Delete "\$SMSTARTUP\\\$\{PRODUCT\}\.lnk"/);

		const on = script({desktop: true, autostart: true});
		assert.match(on, /CreateShortcut "\$DESKTOP\\\$\{PRODUCT\}\.lnk"/);
		assert.match(on, /CreateShortcut "\$SMSTARTUP\\\$\{PRODUCT\}\.lnk"/);
	});

	test('compression', () =>
	{
		assert.match(script({compression: 'fast'}), /SetCompressor zlib/);
		assert.match(script({compression: 'small'}), /SetCompressor \/SOLID lzma/);
	});

	test('no directory page, and the uninstaller removes only what it installed', () =>
	{
		const s = script();

		assert.doesNotMatch(s, /Page directory/);
		assert.doesNotMatch(s, /RMDir \/r "\$INSTDIR"/);
	});

	test('stopping the app waits for its processes before replacing files', () =>
	{
		const s = script();

		assert.match(s, /taskkill\.exe" \/F \/T \/IM "\$\{EXE\}"/);
		assert.match(s, /kill_wait:/);
	});

	test('editor mode: keeps NSIS, no runtime folder, associates .ahmi, asks before stopping', () =>
	{
		const s = script({editor: true, runtimeDir: null, installerIcon: '/b/icon.ico',
			fileAssociation: {ext: 'ahmi', progId: 'AppendHMIStudio.Project', description: 'HMI Application'}});

		assert.match(s, /File \/r "\/t\/resources\/nsis"/);
		assert.doesNotMatch(s, /hmi-runtime/);
		assert.match(s, /WriteRegStr HKCU "Software\\Classes\\\.ahmi" "" "AppendHMIStudio\.Project"/);
		assert.match(s, /shell\\open\\command" "" '"\$INSTDIR\\\$\{EXE\}" "%1"'/);
		assert.match(s, /DeleteRegKey HKCU "Software\\Classes\\AppendHMIStudio\.Project"/);
		assert.match(s, /Icon "\/b\/icon\.ico"/);
		assert.match(s, /is running\. Close it, then click Retry/);
	});

	test('a product name with NSIS syntax in it is quoted', () =>
	{
		const s = script({productName: 'A $B "C"'});
		assert.match(s, /!define PRODUCT "A \$\$B \$\\"C\$\\""/);
	});
});

describe('options', () =>
{
	test('defaults', () =>
	{
		const o = normaliseOptions({productName: ' P ', version: '1.0.0'});

		assert.equal(o.productName, 'P');
		assert.equal(o.scope, 'user');
		assert.equal(o.compression, 'small');
		assert.equal(o.desktop, false);
		assert.equal(o.runtime.windowMode, 'kiosk');
		assert.equal(o.runtime.exit, 'shortcut');
	});

	test('refuses a missing name, a bad version or a password with no hash', () =>
	{
		assert.throws(() => normaliseOptions({productName: '', version: '1.0'}), /product name/);
		assert.throws(() => normaliseOptions({productName: '???', version: '1.0'}), /product name/);
		assert.throws(() => normaliseOptions({productName: 'P', version: 'v1'}), /version/);
		assert.throws(() => normaliseOptions({productName: 'P', version: '1', runtime: {exit: 'password'}}),
			/exit password/);
	});

	test('runtime.json is what the runtime reads, and keeps the password working', () =>
	{
		const dir = tempDir();
		const salt = 'ab12';
		const o = normaliseOptions(Object.assign({}, baseOptions, {runtime: {windowMode: 'window',
			exit: 'password', salt: salt, hash: hashExitPassword(salt, 'pw')}}));

		fs.writeFileSync(path.join(dir, 'runtime.json'), runtimeJson(o));
		const c = loadRuntimeConfig(dir);

		assert.equal(c.productName, 'Line 3 HMI');
		assert.equal(c.windowMode, 'window');
		assert.deepEqual([c.width, c.height], [800, 480]);
		assert.equal(mayExit(c, 'pw'), true);
		assert.equal(mayExit(c, 'nope'), false);
	});

	test('installer file name', () =>
	{
		assert.equal(installerName({productName: 'Line: 3', version: '2.0'}), 'Line 3-2.0-Setup.exe');
	});
});

describe('template and NSIS resolution', () =>
{
	test('a packaged Windows editor is its own template', () =>
	{
		const dir = fakeTemplate();
		const t = resolveTemplate({platform: 'win32', isPackaged: true, execPath: path.join(dir, 'Append HMI Studio.exe')});

		assert.deepEqual(t, {dir: dir, exe: 'Append HMI Studio.exe'});
	});

	test('packaged Linux/macOS editors use resources/runtime/win-x64', () =>
	{
		const res = tempDir();
		fs.cpSync(fakeTemplate(), path.join(res, 'runtime', 'win-x64'), {recursive: true});

		assert.equal(resolveTemplate({platform: 'linux', isPackaged: true, resourcesPath: res}).dir,
			path.join(res, 'runtime', 'win-x64'));
	});

	test('a missing template says how to get one', () =>
	{
		assert.throws(() => resolveTemplate({platform: 'linux', isPackaged: false, appPath: tempDir()}),
			/npm run build-win-runtime/);
		assert.throws(() => resolveTemplate({platform: 'linux', isPackaged: true, resourcesPath: tempDir()}),
			/Reinstall/);
	});

	test('a missing NSIS says how to get it', () =>
	{
		assert.throws(() => resolveNsis({platform: 'linux', isPackaged: false, appPath: tempDir()}),
			/npm run fetch-nsis/);
	});
});

const nsisPresent = (() =>
{
	try
	{
		resolveNsis({platform: process.platform, isPackaged: false, appPath: root});
		return true;
	}
	catch (e)
	{
		return false;
	}
})();

const has7z = spawnSync('7z', ['i'], {encoding: 'utf8'}).status === 0;

describe('scanTemplate', () =>
{
	test('sees app.asar as a file and sizes what a package carries', () =>
	{
		const dir = fakeTemplate();
		const scan = scanTemplate(dir, null);
		const asar = scan.resources.find(e => e.name === 'app.asar');

		assert.equal(asar.dir, false);
		assert.equal(scan.resources.find(e => e.name === 'nsis').dir, true);
		assert.ok(scan.total > 0);
	});

	// Electron's fs shows .asar archives as folders; the editor's main process
	// runs the Publisher there, where a nested archive made publishing fail
	const electronBin = (() =>
	{
		try
		{
			return createRequire(import.meta.url)('electron');
		}
		catch (e)
		{
			return null;
		}
	})();

	test('inside Electron too', {skip: (electronBin == null || !fs.existsSync(electronBin)) && 'no electron'}, async () =>
	{
		const asarLib = createRequire(import.meta.url)('@electron/asar');
		const dir = fakeTemplate();
		const src = tempDir();
		fs.mkdirSync(path.join(src, 'dist'));
		fs.writeFileSync(path.join(src, 'dist', 'inner.txt'), 'x');
		const asarPath = path.join(dir, 'resources', 'app.asar');
		fs.rmSync(asarPath);
		await asarLib.createPackage(src, asarPath);

		const script = path.join(tempDir(), 'scan.mjs');
		fs.writeFileSync(script, 'import {scanTemplate} from ' +
			JSON.stringify(path.join(root, 'src', 'main', 'publish', 'Publisher.js')) + ';\n' +
			'const s = scanTemplate(' + JSON.stringify(dir) + ', null);\n' +
			'console.log(JSON.stringify({asarDir: s.resources.find(e => e.name === "app.asar").dir, total: s.total, ' +
			'noAsar: process.noAsar === true}));\n');

		const r = spawnSync(electronBin, [script], {encoding: 'utf8',
			env: Object.assign({}, process.env, {ELECTRON_RUN_AS_NODE: '1'})});
		assert.equal(r.status, 0, r.stderr);

		const out = JSON.parse(r.stdout.trim().split('\n').pop());
		assert.equal(out.asarDir, false);
		assert.equal(out.noAsar, false, 'process.noAsar is restored');
		// Walking into the archive would count its contents instead of its size
		assert.equal(out.total, scanTemplate(dir, null).total);
	});
});

describe('brandExecutable', {skip: !fs.existsSync(samplePe) && 'run npm run fetch-nsis'}, () =>
{
	test('sets the product name, version and publisher', async () =>
	{
		const out = path.join(tempDir(), 'Line 3.exe');
		await brandExecutable(samplePe, out, {productName: 'Line 3', version: '1.2.0',
			publisher: 'Acme', exeName: 'Line 3.exe'});
		const v = versionStrings(out);

		assert.equal(v.strings.ProductName, 'Line 3');
		assert.equal(v.strings.FileDescription, 'Line 3');
		assert.equal(v.strings.CompanyName, 'Acme');
		assert.equal(v.strings.ProductVersion, '1.2.0');
		assert.equal(v.strings.OriginalFilename, 'Line 3.exe');
	});

	test('replaces the icon', async () =>
	{
		const out = path.join(tempDir(), 'P.exe');
		await brandExecutable(samplePe, out, {productName: 'P', version: '1', exeName: 'P.exe', iconPath: icon});
		const images = resedit.Data.IconFile.from(fs.readFileSync(icon)).icons.length;

		assert.equal(versionStrings(out).icons, images);
	});

	test('checkIcon refuses what is not an .ico', async () =>
	{
		await checkIcon(icon);
		await assert.rejects(checkIcon(samplePe), /Not a usable icon/);
	});
});

describe('Publisher build', {skip: !nsisPresent && 'run npm run fetch-nsis'}, () =>
{
	function publisher(template)
	{
		// A source checkout whose dist-win-runtime is the fake template
		const app = tempDir();
		fs.mkdirSync(path.join(app, 'dist-win-runtime'));
		fs.cpSync(template, path.join(app, 'dist-win-runtime', 'win-unpacked'), {recursive: true});
		fs.symlinkSync(path.join(root, 'build'), path.join(app, 'build'));

		return new Publisher({platform: process.platform, isPackaged: false, appPath: app});
	}

	test('builds an installer with the runtime folder and progress', async () =>
	{
		const pub = publisher(fakeTemplate());
		const out = tempDir();
		const events = [];
		pub.on('progress', (e) => events.push(e));

		const file = await pub.build('<mxfile/>', Object.assign({}, baseOptions, {compression: 'fast'}), out, icon);

		assert.equal(file, path.join(out, 'Line 3 HMI-1.2.0-Setup.exe'));
		assert.equal(fs.readFileSync(file).subarray(0, 2).toString(), 'MZ');
		assert.equal(events[0].stage, 'preparing');
		assert.equal(events[events.length - 1].stage, 'done');
		assert.ok(events.some(e => e.stage === 'compressing' && e.percent > 0));
		assert.equal(pub.busy, false);

		if (has7z)
		{
			const list = spawnSync('7z', ['l', file], {encoding: 'utf8'}).stdout.replace(/\\/g, '/');

			assert.match(list, /Line 3 HMI\.exe/);
			assert.match(list, /resources\/hmi-runtime\/icon\.ico/);
			assert.match(list, /resources\/hmi-runtime\/runtime\.json/);
			assert.match(list, /resources\/hmi-runtime\/project\.ahmi/);
			assert.match(list, /resources\/comms\/hmi-comms\.exe/);
			assert.match(list, /locales\/en-US\.pak/);
			assert.doesNotMatch(list, /Append HMI Studio\.exe|resources\/nsis|resources\/runtime\/|app-update/);
		}
	});

	test('cancel stops makensis and cleans up', async () =>
	{
		const tpl = fakeTemplate();
		// Enough data that makensis is still working when cancelled
		fs.writeFileSync(path.join(tpl, 'resources', 'app.asar'), Buffer.alloc(64 * 1024 * 1024, 7));
		const pub = publisher(tpl);
		const out = tempDir();

		pub.on('progress', (e) =>
		{
			if (e.stage === 'compressing')
			{
				pub.cancel();
			}
		});

		await assert.rejects(pub.build('<mxfile/>', baseOptions, out), /cancelled/);
		assert.deepEqual(fs.readdirSync(out), []);
		assert.equal(pub.busy, false);
	});

	test('one build at a time', async () =>
	{
		const pub = publisher(fakeTemplate());
		const out = tempDir();
		const first = pub.build('<mxfile/>', Object.assign({}, baseOptions, {compression: 'fast'}), out);

		await assert.rejects(pub.build('<mxfile/>', baseOptions, out), /already/);
		await first;
	});
});
