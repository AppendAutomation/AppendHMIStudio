// The product identity: package.json and the electron-builder configs agree
// with src/main/brand.js, there is no updater, and the main process shows no
// upstream (draw.io) name
import { test, describe } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';
import { PRODUCT_NAME, APP_ID, LINUX_EXECUTABLE, WINDOWS_EXE } from '../main/brand.js';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const readJson = (name) => JSON.parse(fs.readFileSync(path.join(root, name), 'utf8'));
const CONFIGS = ['electron-builder-win.json', 'electron-builder-linux.json'];

// Identifiers that are part of file formats or export output, not branding
const ALLOWED = [/vnd\.jgraph\.mxfile/, /viewer\.diagrams\.net/, /convert\.diagrams\.net/,
	/\[jgraph\/drawio(-desktop)?#\d+\]/];
const UPSTREAM = /draw\.io|drawio\.com|diagrams\.net|jgraph/i;

describe('brand', () =>
{
	test('package.json names the product', () =>
	{
		const pkg = readJson('package.json');

		assert.equal(pkg.productName, PRODUCT_NAME);
		assert.equal(pkg.name, LINUX_EXECUTABLE);
		assert.equal(WINDOWS_EXE, PRODUCT_NAME + '.exe');
		assert.equal(pkg.dependencies['electron-updater'], undefined);
		assert.doesNotMatch(JSON.stringify(pkg), UPSTREAM);
	});

	test('the configs agree with brand.js and publish nowhere', () =>
	{
		for (const name of CONFIGS)
		{
			const c = readJson(name);

			assert.equal(c.appId, APP_ID, name);
			assert.equal(c.productName, PRODUCT_NAME, name);
			// null, not absent: electron-builder infers GitHub from package.json
			assert.ok('publish' in c && c.publish === null, name);
			assert.doesNotMatch(JSON.stringify(c), UPSTREAM, name);
		}

		const linux = readJson('electron-builder-linux.json');
		assert.equal(linux.linux.executableName, LINUX_EXECUTABLE);
		assert.equal(linux.linux.desktop.entry.StartupWMClass, LINUX_EXECUTABLE);
		assert.deepEqual(linux.fileAssociations.map(a => a.ext), ['ahmi']);
	});

	test('the main process shows no upstream name', () =>
	{
		const found = [];
		const walk = (dir) =>
		{
			for (const e of fs.readdirSync(dir, {withFileTypes: true}))
			{
				const p = path.join(dir, e.name);

				if (e.isDirectory())
				{
					walk(p);
				}
				else if (p.endsWith('.js'))
				{
					fs.readFileSync(p, 'utf8').split('\n').forEach((line, i) =>
					{
						const code = line.trim();

						// Comments are for developers (upstream issue references)
						if (code.startsWith('//') || code.startsWith('*') || code.startsWith('/*'))
						{
							return;
						}

						const stripped = ALLOWED.reduce((s, re) => s.replace(new RegExp(re, 'g'), ''),
							code.replace(/\/\/.*$/, ''));

						if (UPSTREAM.test(stripped))
						{
							found.push(path.relative(root, p) + ':' + (i + 1) + ': ' + code);
						}
					});
				}
			}
		};

		walk(path.join(root, 'src', 'main'));
		assert.deepEqual(found, []);
	});
});
