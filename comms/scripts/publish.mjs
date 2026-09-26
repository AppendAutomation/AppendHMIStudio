// Publishes hmi-comms as a self-contained single-file executable per runtime.
//
//   node comms/scripts/publish.mjs              host platform and architecture
//   node comms/scripts/publish.mjs --rid linux-arm64
//   node comms/scripts/publish.mjs --all        every shipped runtime
//
// Output lands where the electron-builder configs pick it up as
// extraResources: comms/publish/<os>-<arch>/, with both macOS architectures
// under comms/publish/mac/ so a universal build carries both.

import {spawnSync} from 'child_process';
import {fileURLToPath} from 'url';
import fs from 'fs';
import path from 'path';

const commsDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const project = path.join(commsDir, 'src', 'Hmi.Comms.Server');

const RIDS = {
	'win-x64': 'win-x64',
	'win-arm64': 'win-arm64',
	'linux-x64': 'linux-x64',
	'linux-arm64': 'linux-arm64',
	'osx-x64': path.join('mac', 'osx-x64'),
	'osx-arm64': path.join('mac', 'osx-arm64')
};

function hostRid()
{
	const os = {win32: 'win', darwin: 'osx', linux: 'linux'}[process.platform];
	const arch = {x64: 'x64', arm64: 'arm64'}[process.arch];

	if (os == null || arch == null)
	{
		throw new Error(`No hmi-comms runtime for ${process.platform}/${process.arch}`);
	}

	return os + '-' + arch;
}

function parseArgs(argv)
{
	const rids = [];

	for (let i = 0; i < argv.length; i++)
	{
		if (argv[i] === '--all')
		{
			rids.push(...Object.keys(RIDS));
		}
		else if (argv[i] === '--rid')
		{
			rids.push(argv[++i]);
		}
		else if (argv[i] === '--mac')
		{
			rids.push('osx-x64', 'osx-arm64');
		}
		else
		{
			throw new Error(`Unknown argument ${argv[i]}`);
		}
	}

	return (rids.length > 0) ? [...new Set(rids)] : [hostRid()];
}

function publish(rid)
{
	const sub = RIDS[rid];

	if (sub == null)
	{
		throw new Error(`Unknown runtime ${rid}; one of ${Object.keys(RIDS).join(', ')}`);
	}

	const out = path.join(commsDir, 'publish', sub);
	fs.rmSync(out, {recursive: true, force: true});

	console.log(`hmi-comms: publishing ${rid} -> ${path.relative(process.cwd(), out)}`);

	const res = spawnSync('dotnet', ['publish', project,
		'-c', 'Release',
		'-r', rid,
		'--self-contained',
		'-p:PublishSingleFile=true',
		'-p:PublishTrimmed=true',
		'-p:TrimMode=partial',
		'-p:InvariantGlobalization=true',
		'-p:DebugType=none',
		'-p:DebugSymbols=false',
		'-o', out], {stdio: 'inherit', shell: process.platform === 'win32'});

	if (res.status !== 0)
	{
		throw new Error(`dotnet publish failed for ${rid}`);
	}
}

try
{
	for (const rid of parseArgs(process.argv.slice(2)))
	{
		publish(rid);
	}
}
catch (e)
{
	console.error(e.message);
	process.exit(1);
}
