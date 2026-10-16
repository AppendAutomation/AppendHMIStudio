// SQL database access — exercises src/main/sql/
//
// SQLite is tested for real, against temporary files. SQL Server needs a
// server, so its tests run only when HMI_SQL_HOST, HMI_SQL_USER and
// HMI_SQL_PASS are set and are skipped otherwise, the way the comms tests skip
// without HMI_COMMS_EXE. No host, user or password belongs in this file.
import { test, describe, after, before } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'fs';
import os from 'os';
import path from 'path';
import * as client from '../main/sql/SqlClient.js';
import * as codes from '../main/sql/ResultCodes.js';
import * as secret from '../main/sql/Secret.js';
import * as store from '../main/sql/DataSourceStore.js';
import { SqlSession, validateSqlArgs } from '../main/sql/SqlSession.js';

const tempDirs = [];

function tempDir()
{
	const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'hmi-sql-test-'));
	tempDirs.push(dir);

	return dir;
}

function envFor(dir)
{
	return {userData: path.join(dir, 'userData'), documents: path.join(dir, 'Documents')};
}

after(() =>
{
	for (const dir of tempDirs)
	{
		fs.rmSync(dir, {recursive: true, force: true});
	}
});

describe('result codes', () =>
{
	test('every code has a message', () =>
	{
		for (const name of Object.keys(codes))
		{
			const value = codes[name];

			if (typeof value === 'number')
			{
				assert.ok(codes.MESSAGES[value] != null, name + ' (' + value + ') has no message');
			}
		}
	});

	test('a code we do not know still reads sensibly', () =>
	{
		assert.equal(codes.message(-4242), 'Result code -4242');
		assert.equal(codes.message('nonsense'), codes.MESSAGES[codes.INTERNAL]);
	});

	test('the truncation warning is not reused: our message tags have no limit', () =>
	{
		assert.equal(codes.MESSAGES[-1007], undefined);
	});

	test('codeOf falls back to the provider error', () =>
	{
		assert.equal(codes.codeOf(new Error('plain')), codes.PROVIDER_ERROR);
		assert.equal(codes.codeOf(new codes.SqlError(codes.NO_BIND_LIST)), codes.NO_BIND_LIST);
	});
});

// The renderer cannot import from the main process, so it keeps its own copy
// of the codes and their messages. Nothing but a test stops the two drifting.
describe('the renderer result codes match the main process', () =>
{
	const rendererSource = fs.readFileSync(path.join(
		import.meta.dirname, '..', '..', 'drawio', 'src', 'main', 'webapp', 'js', 'hmi', 'HmiSql.js'), 'utf8');

	const block = (name) =>
	{
		const at = rendererSource.indexOf('HmiSql.' + name + ' = {');
		assert.ok(at >= 0, 'HmiSql.' + name + ' not found');
		const end = rendererSource.indexOf('\n};', at);

		return rendererSource.substring(at, end);
	};

	test('every code has the same number on both sides', () =>
	{
		const text = block('CODES');

		for (const [name, value] of Object.entries(codes))
		{
			if (typeof value !== 'number') { continue; }

			const found = text.match(new RegExp('\\b' + name + ':\\s*(-?\\d+)'));
			assert.ok(found, name + ' is missing from the renderer\'s HmiSql.CODES');
			assert.equal(Number(found[1]), value, name + ' differs');
		}
	});

	test('every message is word for word the same', () =>
	{
		const text = block('MESSAGES');

		for (const [number, message] of Object.entries(codes.MESSAGES))
		{
			const found = text.match(new RegExp("'?" + number + "'?:\\s*(\"[^\"]*\"|'[^']*')"));
			assert.ok(found, 'code ' + number + ' is missing from the renderer\'s HmiSql.MESSAGES');
			assert.equal(found[1].slice(1, -1), message, 'code ' + number + ' differs');
		}
	});

	test('the renderer adds no code of its own', () =>
	{
		const text = block('CODES');
		const names = [...text.matchAll(/\n\t([A-Z_]+):/g)].map((m) => m[1]);

		for (const name of names)
		{
			assert.ok(codes[name] != null, name + ' is in the renderer only');
		}
	});
});

describe('password wrapping', () =>
{
	test('round trips', () =>
	{
		const wrapped = secret.wrap('s3cret pass');
		assert.notEqual(wrapped, 's3cret pass');
		assert.ok(secret.isWrapped(wrapped));
		assert.equal(secret.unwrap(wrapped), 's3cret pass');
	});

	test('the same password wraps differently each time', () =>
	{
		assert.notEqual(secret.wrap('abc'), secret.wrap('abc'));
	});

	test('an empty password stays empty and wrapping is idempotent', () =>
	{
		assert.equal(secret.wrap(''), '');
		assert.equal(secret.wrap(null), '');
		const once = secret.wrap('abc');
		assert.equal(secret.wrap(once), once);
	});

	test('a hand-written plain password still works', () =>
	{
		assert.equal(secret.unwrap('plain text'), 'plain text');
		assert.equal(secret.unwrap(''), '');
		assert.equal(secret.unwrap(null), '');
	});

	test('a damaged wrapper reads as no password rather than throwing', () =>
	{
		assert.equal(secret.unwrap('hmi1:not-base64-at-all'), '');
	});

	test('unicode survives', () =>
	{
		assert.equal(secret.unwrap(secret.wrap('pässwörd✓')), 'pässwörd✓');
	});
});

describe('checkSpec', () =>
{
	test('a SQLite source carries a folder choice, defaulting to the app data folder', () =>
	{
		assert.equal(client.checkSpec({name: 'A', provider: 'sqlite', file: 'a.db'}).folder, 'appdata');
		assert.equal(client.checkSpec({name: 'A', provider: 'sqlite', file: 'a.db',
			folder: 'Documents'}).folder, 'documents');
		assert.equal(client.checkSpec({name: 'A', provider: 'sqlite', file: 'a.db',
			folder: 'nonsense'}).folder, 'appdata');
	});

	test('normalizes SQL Server defaults', () =>
	{
		const spec = client.checkSpec({name: 'Plant', provider: 'mssql', host: 'db', database: 'D'});
		assert.equal(spec.port, 1433);
		assert.equal(spec.timeoutMs, client.DEFAULTS.timeoutMs);
		assert.equal(spec.options.encrypt, false);
		assert.equal(spec.options.trustServerCertificate, true);
		assert.equal(spec.options.legacyTls, false);
	});

	test('keeps the encryption switches a plant server needs', () =>
	{
		const spec = client.checkSpec({name: 'Old', provider: 'mssql', host: 'db',
			options: {encrypt: true, legacyTls: true, trustServerCertificate: false}});
		assert.deepEqual({e: spec.options.encrypt, l: spec.options.legacyTls,
			t: spec.options.trustServerCertificate}, {e: true, l: true, t: false});
	});

	test('refuses a provider we do not have', () =>
	{
		assert.throws(() => client.checkSpec({name: 'x', provider: 'oracle', host: 'h'}),
			(e) => e.code === codes.PROVIDER_UNAVAILABLE);
	});

	test('refuses nonsense', () =>
	{
		assert.throws(() => client.checkSpec(null), /bad arg: dataSource/);
		assert.throws(() => client.checkSpec({name: 'x', provider: 'mssql'}), /bad arg: host/);
		assert.throws(() => client.checkSpec({name: 'x', provider: 'mssql', host: 'h', port: 0}),
			/bad arg: port/);
		assert.throws(() => client.checkSpec({name: 'x', provider: 'sqlite'}), /bad arg: file/);
		assert.throws(() => client.checkSpec({name: 'x', provider: 'mssql', host: 'h', timeoutMs: 5}),
			/bad arg: timeoutMs/);
	});
});

describe('resolveFile', () =>
{
	const env = {userData: '/var/app', documents: '/home/u/Documents'};

	test('a bare name belongs to the application data folder', () =>
	{
		assert.equal(client.resolveFile('batches.db', env), path.join('/var/app', 'databases', 'batches.db'));
		assert.equal(client.resolveFile('batches.db', env, 'appdata'),
			path.join('/var/app', 'databases', 'batches.db'));
	});

	test('the folder choice puts the same name in Documents', () =>
	{
		// A project carries a name and a folder, never a path: an absolute
		// path names one machine's account, and a published application runs
		// on another PC
		assert.equal(client.resolveFile('batches.db', env, 'documents'),
			path.join('/home/u/Documents', 'batches.db'));
		assert.equal(client.resolveFile('sub/batches.db', env, 'documents'),
			path.join('/home/u/Documents', 'sub', 'batches.db'));
	});

	test('with no Documents folder the choice is refused, not silently moved', () =>
	{
		assert.throws(() => client.resolveFile('x.db', {userData: '/var/app'}, 'documents'),
			(e) => e.code === codes.FILE_NOT_ALLOWED && /Documents folder cannot be found/.test(e.message));
	});

	test('an absolute path inside either folder is still accepted', () =>
	{
		assert.equal(client.resolveFile('/home/u/Documents/plant.db', env), '/home/u/Documents/plant.db');
		assert.equal(client.resolveFile('/home/u/Documents/sub/plant.db', env), '/home/u/Documents/sub/plant.db');
		assert.equal(client.resolveFile('/var/app/databases/x.db', env, 'documents'),
			'/var/app/databases/x.db');
	});

	test('anywhere else is refused', () =>
	{
		for (const bad of ['/etc/passwd', '/home/u/other.db', '/var/app/config.json',
			'/home/u/Documents/../secret.db', '/home/u/DocumentsEvil/x.db'])
		{
			assert.throws(() => client.resolveFile(bad, env),
				(e) => e.code === codes.FILE_NOT_ALLOWED, bad + ' was allowed');
		}
	});

	test('with no roots nothing is allowed', () =>
	{
		assert.throws(() => client.resolveFile('x.db', {}), (e) => e.code === codes.FILE_NOT_ALLOWED);
	});
});

describe('value conversion', () =>
{
	test('dates become local-time text, milliseconds only when there are any', () =>
	{
		assert.equal(client.dateText(new Date(2026, 9, 16, 13, 4, 5, 0)), '2026-10-16 13:04:05');
		assert.equal(client.dateText(new Date(2026, 9, 16, 13, 4, 5, 70)), '2026-10-16 13:04:05.070');
	});

	test('the first nineteen characters are always the date and time', () =>
	{
		const withMs = client.dateText(new Date(2026, 0, 2, 3, 4, 5, 6));
		assert.equal(withMs.slice(0, 19), '2026-01-02 03:04:05');
	});

	test('tags can hold whatever comes back', () =>
	{
		assert.equal(client.cellValue(null), null);
		assert.equal(client.cellValue(undefined), null);
		assert.equal(client.cellValue(12.5), 12.5);
		assert.equal(client.cellValue(true), true);
		assert.equal(client.cellValue('text'), 'text');
		assert.equal(client.cellValue(new Uint8Array([0x41, 0x42])), '4142');
		assert.equal(client.cellValue(new Date(NaN)), null);
	});

	test('a big integer keeps its digits', () =>
	{
		assert.equal(client.cellValue(BigInt(42)), 42);
		assert.equal(client.cellValue(BigInt('9007199254740993')), '9007199254740993');
		assert.equal(client.cellValue(BigInt('-9007199254740993')), '-9007199254740993');
	});
});

describe('bindPlaceholders', () =>
{
	test('SQLite takes ? as it is', () =>
	{
		const out = client.bindPlaceholders('SELECT * FROM t WHERE a = ?', 'sqlite');
		assert.equal(out.sql, 'SELECT * FROM t WHERE a = ?');
		assert.equal(out.names, null);
	});

	test('SQL Server gets numbered parameters', () =>
	{
		const out = client.bindPlaceholders('SELECT * FROM t WHERE a = ? AND b = ?', 'mssql');
		assert.equal(out.sql, 'SELECT * FROM t WHERE a = @p1 AND b = @p2');
		assert.deepEqual(out.names, ['p1', 'p2']);
	});

	test('a question mark inside a string, a name or a comment is left alone', () =>
	{
		const cases = [
			["SELECT 'why?' FROM t WHERE a = ?", "SELECT 'why?' FROM t WHERE a = @p1"],
			["SELECT 'it''s? ok' , ?", "SELECT 'it''s? ok' , @p1"],
			['SELECT [odd?name], ?', 'SELECT [odd?name], @p1'],
			['SELECT "quoted?", ?', 'SELECT "quoted?", @p1'],
			['SELECT 1 -- why?\nWHERE a = ?', 'SELECT 1 -- why?\nWHERE a = @p1'],
			['SELECT /* huh? */ ?', 'SELECT /* huh? */ @p1']
		];

		for (const [input, expected] of cases)
		{
			assert.equal(client.bindPlaceholders(input, 'mssql').sql, expected, input);
		}
	});

	test('an unterminated string does not lose the rest of the statement', () =>
	{
		assert.equal(client.bindPlaceholders("SELECT 'oops", 'mssql').sql, "SELECT 'oops");
	});
});

describe('the per-PC credential store', () =>
{
	test('saves, summarizes and never gives the password back', async () =>
	{
		const base = path.join(tempDir(), 'userData');
		assert.deepEqual(await store.load(base, 'Proj'), {});

		await store.save(base, 'Proj', {Plant: {user: 'reporter', password: 'hunter2'}});

		const saved = await store.load(base, 'Proj');
		assert.ok(secret.isWrapped(saved.Plant.password), 'the password is stored wrapped');
		assert.equal(secret.unwrap(saved.Plant.password), 'hunter2');

		const summary = await store.summary(base, 'Proj');
		assert.deepEqual(summary, [{name: 'Plant', user: 'reporter', hasPassword: true}]);
		assert.ok(!JSON.stringify(summary).includes('hunter2'), 'a summary carries no password');

		const file = await fs.promises.readFile(store.storeFile(base, 'Proj'), 'utf8');
		assert.ok(!file.includes('hunter2'), 'the file carries no plain password');
	});

	test('one data source can be set without disturbing the others', async () =>
	{
		const base = path.join(tempDir(), 'userData');
		await store.save(base, 'Proj', {A: {user: 'a', password: 'pa'}, B: {user: 'b', password: 'pb'}});
		await store.setOverride(base, 'Proj', 'B', {user: 'b2', password: 'pb2'});

		const after = await store.load(base, 'Proj');
		assert.equal(secret.unwrap(after.A.password), 'pa');
		assert.equal(after.B.user, 'b2');
		assert.equal(secret.unwrap(after.B.password), 'pb2');

		await store.setOverride(base, 'Proj', 'A', null);
		assert.deepEqual(Object.keys(await store.load(base, 'Proj')), ['B']);
	});

	test('an override replaces only what it carries', () =>
	{
		const spec = {name: 'Plant', user: 'project', password: 'projectpw'};
		assert.equal(store.applyOverride(spec, {}).user, 'project');
		assert.equal(store.applyOverride(spec, {Plant: {user: '', password: secret.wrap('pcpw')}}).user,
			'project', 'an empty user leaves the project alone');
		assert.equal(secret.unwrap(store.applyOverride(spec,
			{Plant: {user: '', password: secret.wrap('pcpw')}}).password), 'pcpw');
		assert.equal(store.applyOverride(spec, {Plant: {user: 'pcuser', password: ''}}).password,
			'projectpw', 'an empty password leaves the project alone');
	});

	test('clears', async () =>
	{
		const base = path.join(tempDir(), 'userData');
		await store.save(base, 'Proj', {A: {user: 'a', password: 'p'}});
		await store.clear(base, 'Proj');
		assert.deepEqual(await store.load(base, 'Proj'), {});
	});

	test('refuses nonsense', async () =>
	{
		assert.throws(() => store.checkOverrides(null), /bad arg: overrides/);
		assert.throws(() => store.checkOverrides({A: 'string'}), /bad arg: overrides/);
		assert.throws(() => store.checkOverrides({A: {password: 'x'.repeat(5000)}}), /bad arg: overrides/);
		assert.throws(() => store.checkOverrides({'': {password: 'x'}}), /bad arg: overrides/);
	});
});

describe('SQLite end to end', () =>
{
	let dir;
	let env;
	let conn;

	before(async () =>
	{
		dir = tempDir();
		env = envFor(dir);
		fs.mkdirSync(env.documents, {recursive: true});
		conn = await client.open({name: 'Demo', provider: 'sqlite', file: 'batches.db'}, env);
	});

	after(async () =>
	{
		await client.close(conn);
	});

	test('the file lands in the application data folder', () =>
	{
		assert.equal(conn.file, path.join(env.userData, 'databases', 'batches.db'));
		assert.ok(fs.existsSync(conn.file));
	});

	test('creates a table, inserts and selects back', async () =>
	{
		const created = await client.exec(conn, {sql: 'CREATE TABLE BatchLog (Id INTEGER PRIMARY KEY, ' +
			'Name TEXT, Target REAL, Note TEXT, Stamp TEXT)'});
		assert.deepEqual(created.columns, []);

		const inserted = await client.exec(conn, {sql: 'INSERT INTO BatchLog (Name, Target, Note, Stamp) ' +
			'VALUES (?, ?, ?, ?)', params: [{type: 'varchar', value: 'Blend A'},
			{type: 'float', value: 1250.5}, {type: 'varchar', value: 'fine'},
			{type: 'varchar', value: '2026-10-16 13:04:05'}]});
		assert.equal(inserted.rowsAffected, 1);

		const got = await client.exec(conn, {sql: 'SELECT Id, Name, Target, Stamp FROM BatchLog'});
		assert.deepEqual(got.columns.map((c) => c.name), ['Id', 'Name', 'Target', 'Stamp']);
		assert.deepEqual(got.rows, [[1, 'Blend A', 1250.5, '2026-10-16 13:04:05']]);
	});

	test('a string far longer than the old message limit survives whole', async () =>
	{
		const long = 'x'.repeat(5000);
		await client.exec(conn, {sql: 'INSERT INTO BatchLog (Name, Note) VALUES (?, ?)',
			params: [{type: 'varchar', value: 'Long'}, {type: 'varchar', value: long}]});

		const got = await client.exec(conn, {sql: "SELECT Note FROM BatchLog WHERE Name = 'Long'"});
		assert.equal(got.rows[0][0].length, 5000);
	});

	test('null comes back as null', async () =>
	{
		const got = await client.exec(conn, {sql: "SELECT Target FROM BatchLog WHERE Name = 'Long'"});
		assert.equal(got.rows[0][0], null);
	});

	test('the row limit truncates and says so', async () =>
	{
		for (let i = 0; i < 5; i++)
		{
			await client.exec(conn, {sql: 'INSERT INTO BatchLog (Name) VALUES (?)',
				params: [{type: 'varchar', value: 'Row' + i}]});
		}

		const capped = await client.exec(conn, {sql: 'SELECT Id FROM BatchLog', maxRows: 2});
		assert.equal(capped.rows.length, 2);
		assert.equal(capped.truncated, true);

		const whole = await client.exec(conn, {sql: 'SELECT Id FROM BatchLog'});
		assert.equal(whole.truncated, false);
	});

	test('an update reports how many rows it changed', async () =>
	{
		const res = await client.exec(conn, {sql: "UPDATE BatchLog SET Target = 1 WHERE Name LIKE 'Row%'"});
		assert.equal(res.rowsAffected, 5);
	});

	test('a bad statement is a provider error carrying the reason', async () =>
	{
		await assert.rejects(() => client.exec(conn, {sql: 'SELECT * FROM NoSuchTable'}),
			(e) => e.code === codes.PROVIDER_ERROR && /NoSuchTable/.test(e.message));
	});

	test('an empty statement has its own code', async () =>
	{
		await assert.rejects(() => client.exec(conn, {sql: '   '}),
			(e) => e.code === codes.EMPTY_STATEMENT);
	});

	test('a transaction commits', async () =>
	{
		await client.begin(conn);
		assert.equal(conn.inTransaction, true);
		await client.exec(conn, {sql: "INSERT INTO BatchLog (Name) VALUES ('Kept')"});
		await client.commit(conn);
		assert.equal(conn.inTransaction, false);

		const got = await client.exec(conn, {sql: "SELECT COUNT(*) FROM BatchLog WHERE Name = 'Kept'"});
		assert.equal(got.rows[0][0], 1);
	});

	test('a transaction rolls back', async () =>
	{
		await client.begin(conn);
		await client.exec(conn, {sql: "INSERT INTO BatchLog (Name) VALUES ('Dropped')"});
		await client.rollback(conn);

		const got = await client.exec(conn, {sql: "SELECT COUNT(*) FROM BatchLog WHERE Name = 'Dropped'"});
		assert.equal(got.rows[0][0], 0);
	});

	test('nested or absent transactions are refused', async () =>
	{
		await assert.rejects(() => client.commit(conn), (e) => e.code === codes.TRANSACTION_STATE);
		await client.begin(conn);
		await assert.rejects(() => client.begin(conn), (e) => e.code === codes.TRANSACTION_STATE);
		await client.rollback(conn);
	});

	test('probe answers with the version', async () =>
	{
		const res = await client.probe({name: 'Demo', provider: 'sqlite', file: 'batches.db'}, env);
		assert.equal(res.ok, true);
		assert.match(res.version, /^\d+\./);
	});

	test('probe refuses a file outside the allowed folders', async () =>
	{
		const res = await client.probe({name: 'Bad', provider: 'sqlite', file: '/etc/shadow'}, env);
		assert.equal(res.ok, false);
		assert.equal(res.code, codes.FILE_NOT_ALLOWED);
	});
});

describe('SqlSession', () =>
{
	test('numbers connections from one and keeps them apart', async () =>
	{
		const dir = tempDir();
		const session = new SqlSession(envFor(dir));

		const first = await session.connect({dataSource: {name: 'A', provider: 'sqlite', file: 'a.db'}});
		const second = await session.connect({dataSource: {name: 'B', provider: 'sqlite', file: 'b.db'}});
		assert.equal(first.connectionId, 1);
		assert.equal(second.connectionId, 2);
		assert.equal(first.code, codes.OK);

		await session.exec({connectionId: 1, sql: 'CREATE TABLE only_in_a (x)'});
		const missing = await session.exec({connectionId: 2, sql: 'SELECT * FROM only_in_a'});
		assert.equal(missing.code, codes.PROVIDER_ERROR);

		await session.closeAll();
	});

	test('a closed connection number is no longer valid, and numbers are not reused', async () =>
	{
		const session = new SqlSession(envFor(tempDir()));
		const opened = await session.connect({dataSource: {name: 'A', provider: 'sqlite', file: 'a.db'}});
		assert.equal((await session.close({connectionId: opened.connectionId})).code, codes.OK);
		assert.equal((await session.exec({connectionId: opened.connectionId, sql: 'SELECT 1'})).code,
			codes.INVALID_CONNECTION);
		assert.equal((await session.close({connectionId: opened.connectionId})).code,
			codes.INVALID_CONNECTION);

		const next = await session.connect({dataSource: {name: 'A', provider: 'sqlite', file: 'a.db'}});
		assert.equal(next.connectionId, 2);
		await session.closeAll();
	});

	test('a connection that cannot open answers in a code, not an exception', async () =>
	{
		const session = new SqlSession(envFor(tempDir()));
		const res = await session.connect({dataSource: {name: 'X', provider: 'sqlite', file: '/etc/shadow'}});
		assert.equal(res.code, codes.FILE_NOT_ALLOWED);
		assert.equal(res.connectionId, undefined);
		await session.closeAll();
	});

	test('too many connections is refused', async () =>
	{
		const session = new SqlSession(envFor(tempDir()));

		for (let i = 0; i < 8; i++)
		{
			const res = await session.connect({dataSource: {name: 'A', provider: 'sqlite', file: 'a' + i + '.db'}});
			assert.equal(res.code, codes.OK, 'connection ' + i);
		}

		const over = await session.connect({dataSource: {name: 'A', provider: 'sqlite', file: 'over.db'}});
		assert.equal(over.code, codes.TOO_MANY_CONNECTIONS);
		await session.closeAll();
	});

	test('the per-PC password is applied when a store is named', async () =>
	{
		const dir = tempDir();
		const env = envFor(dir);
		await store.save(env.userData, 'Proj', {A: {user: 'pcuser', password: 'pcpw'}});

		const session = new SqlSession(env);
		// SQLite ignores credentials, so this checks the resolution path runs
		// and leaves the connection usable
		const res = await session.connect({store: 'Proj',
			dataSource: {name: 'A', provider: 'sqlite', file: 'a.db'}});
		assert.equal(res.code, codes.OK);
		await session.closeAll();
	});

	test('closeAll rolls an open transaction back and refuses later work', async () =>
	{
		const dir = tempDir();
		const session = new SqlSession(envFor(dir));
		const opened = await session.connect({dataSource: {name: 'A', provider: 'sqlite', file: 'a.db'}});
		await session.exec({connectionId: opened.connectionId, sql: 'CREATE TABLE t (x)'});
		await session.transaction('begin', {connectionId: opened.connectionId});
		await session.exec({connectionId: opened.connectionId, sql: 'INSERT INTO t VALUES (1)'});
		await session.closeAll();

		assert.equal((await session.connect({dataSource: {name: 'A', provider: 'sqlite', file: 'a.db'}})).code,
			codes.INVALID_CONNECTION);

		// Reopen and look: the uncommitted row is not there
		const again = new SqlSession(envFor(dir));
		const re = await again.connect({dataSource: {name: 'A', provider: 'sqlite', file: 'a.db'}});
		const got = await again.exec({connectionId: re.connectionId, sql: 'SELECT COUNT(*) FROM t'});
		assert.equal(got.rows[0][0], 0);
		await again.closeAll();
	});

	test('transactions answer in codes', async () =>
	{
		const session = new SqlSession(envFor(tempDir()));
		const opened = await session.connect({dataSource: {name: 'A', provider: 'sqlite', file: 'a.db'}});
		assert.equal((await session.transaction('commit', {connectionId: opened.connectionId})).code,
			codes.TRANSACTION_STATE);
		assert.equal((await session.transaction('begin', {connectionId: 99})).code,
			codes.INVALID_CONNECTION);
		await session.closeAll();
	});
});

describe('validateSqlArgs', () =>
{
	test('accepts what the renderer legitimately sends', () =>
	{
		validateSqlArgs('hmiSql.connect', {dataSource: {provider: 'sqlite', file: 'a.db'}});
		validateSqlArgs('hmiSql.connect', {dataSource: {provider: 'sqlite', file: 'a.db'}, store: 'Proj'});
		validateSqlArgs('hmiSql.exec', {connectionId: 1, sql: 'SELECT 1'});
		validateSqlArgs('hmiSql.exec', {connectionId: 1, sql: 'SELECT ?', params: [{type: 'int', value: 1}],
			maxRows: 10});
		validateSqlArgs('hmiSql.close', {connectionId: 2});
		validateSqlArgs('hmiSql.begin', {connectionId: 2});
		validateSqlArgs('hmiSql.probe', {dataSource: {provider: 'sqlite', file: 'a.db'}});
		validateSqlArgs('hmiSql.overrides', {store: 'Proj'});
		validateSqlArgs('hmiSql.setOverride', {store: 'Proj', name: 'A', override: {password: 'x'}});
		validateSqlArgs('hmiSql.setOverride', {store: 'Proj', name: 'A', override: null});
		validateSqlArgs('hmiSql.wrapSecret', {password: 'x'});
	});

	test('refuses what it should', () =>
	{
		assert.throws(() => validateSqlArgs('hmiSql.connect', {}), /bad arg: dataSource/);
		assert.throws(() => validateSqlArgs('hmiSql.connect',
			{dataSource: {}, store: 7}), /bad arg: store/);
		assert.throws(() => validateSqlArgs('hmiSql.exec', {connectionId: 1}), /bad arg: sql/);
		assert.throws(() => validateSqlArgs('hmiSql.exec',
			{connectionId: 0, sql: 'x'}), /bad arg: connectionId/);
		assert.throws(() => validateSqlArgs('hmiSql.exec',
			{connectionId: 1, sql: 'x', maxRows: 0}), /bad arg: maxRows/);
		assert.throws(() => validateSqlArgs('hmiSql.exec',
			{connectionId: 1, sql: 'x', maxRows: 1000000}), /bad arg: maxRows/);
		assert.throws(() => validateSqlArgs('hmiSql.exec',
			{connectionId: 1, sql: 'x', params: 'no'}), /bad arg: params/);
		assert.throws(() => validateSqlArgs('hmiSql.close', {}), /bad arg: connectionId/);
		assert.throws(() => validateSqlArgs('hmiSql.setOverride', {store: 'P', name: ''}), /bad arg: name/);
		assert.throws(() => validateSqlArgs('hmiSql.unknownThing', {}), /unknown action/);
	});

	test('the exec checks also cover the connection number', () =>
	{
		assert.throws(() => validateSqlArgs('hmiSql.exec', {sql: 'SELECT 1'}), /bad arg: connectionId/);
	});
});

// SQL Server, only with a server to talk to
const liveHost = process.env.HMI_SQL_HOST;
const liveSpec = () => ({name: 'Live', provider: 'mssql', host: liveHost,
	database: process.env.HMI_SQL_DATABASE || 'AppendHmiTest',
	user: process.env.HMI_SQL_USER, password: process.env.HMI_SQL_PASS,
	options: {encrypt: process.env.HMI_SQL_ENCRYPT === '1', legacyTls: process.env.HMI_SQL_LEGACY_TLS === '1'}});

describe('SQL Server', {skip: !liveHost ? 'set HMI_SQL_HOST, HMI_SQL_USER and HMI_SQL_PASS' : false}, () =>
{
	const table = 'HmiSqlTest_' + process.pid;
	let conn;

	before(async () =>
	{
		conn = await client.open(liveSpec(), envFor(tempDir()));
		await client.exec(conn, {sql: 'CREATE TABLE ' + table + ' (Id int IDENTITY(1,1) PRIMARY KEY, ' +
			'Name varchar(80), Target float, Note varchar(max), Stamp datetime, Flag bit)'});
	});

	after(async () =>
	{
		if (conn != null)
		{
			await client.exec(conn, {sql: "IF OBJECT_ID('" + table + "','U') IS NOT NULL DROP TABLE " + table});
			await client.close(conn);
		}
	});

	test('inserts with parameters and reads back', async () =>
	{
		const inserted = await client.exec(conn, {sql: 'INSERT INTO ' + table +
			' (Name, Target, Note, Stamp, Flag) VALUES (?, ?, ?, ?, ?)',
			params: [{type: 'varchar', value: 'Blend A'}, {type: 'float', value: 1250.5},
				{type: 'varchar', value: 'y'.repeat(4000)},
				{type: 'datetime', value: '2026-10-16 13:04:05'}, {type: 'int', value: 1}]});
		assert.equal(inserted.rowsAffected, 1);

		const got = await client.exec(conn, {sql: 'SELECT Name, Target, Note, Stamp, Flag FROM ' + table});
		assert.equal(got.rows.length, 1);
		const [name, target, note, stamp, flag] = got.rows[0];
		assert.equal(name, 'Blend A');
		assert.equal(target, 1250.5);
		assert.equal(note.length, 4000, 'a long string is not truncated');
		assert.equal(stamp, '2026-10-16 13:04:05', 'a local time round trips as itself');
		assert.equal(flag, true);
	});

	test('nulls read as null', async () =>
	{
		await client.exec(conn, {sql: 'INSERT INTO ' + table + ' (Name) VALUES (?)',
			params: [{type: 'varchar', value: 'Bare'}]});
		const got = await client.exec(conn, {sql: 'SELECT Target FROM ' + table + " WHERE Name = 'Bare'"});
		assert.equal(got.rows[0][0], null);
	});

	test('a bad statement carries the database wording', async () =>
	{
		await assert.rejects(() => client.exec(conn, {sql: 'SELECT * FROM NoSuchTable_xyz'}),
			(e) => e.code === codes.PROVIDER_ERROR && /NoSuchTable_xyz/.test(e.message));
	});

	test('a transaction rolls back', async () =>
	{
		await client.begin(conn);
		await client.exec(conn, {sql: 'INSERT INTO ' + table + " (Name) VALUES ('Dropped')"});
		await client.rollback(conn);
		const got = await client.exec(conn, {sql: 'SELECT COUNT(*) FROM ' + table + " WHERE Name = 'Dropped'"});
		assert.equal(got.rows[0][0], 0);
	});

	test('probe answers with the server version', async () =>
	{
		const res = await client.probe(liveSpec(), envFor(tempDir()));
		assert.equal(res.ok, true);
		assert.match(res.version, /SQL Server/);
	});

	test('a wrong password fails to connect, in a code', async () =>
	{
		const res = await client.probe(Object.assign(liveSpec(), {password: 'definitely-not-it'}),
			envFor(tempDir()));
		assert.equal(res.ok, false);
		assert.equal(res.code, codes.CONNECT_FAILED);
	});

	test('an unreachable host fails to connect rather than timing out', async () =>
	{
		// The driver reports a connect timeout and a slow statement with the
		// same code; they must not read alike to whoever gets the message
		const res = await client.probe(Object.assign(liveSpec(),
			{host: '192.0.2.1', timeoutMs: 2000}), envFor(tempDir()));
		assert.equal(res.ok, false);
		assert.equal(res.code, codes.CONNECT_FAILED);
	});

	test('a statement that outstays its welcome times out', async () =>
	{
		const slow = await client.open(Object.assign(liveSpec(), {timeoutMs: 1000}), envFor(tempDir()));

		try
		{
			await assert.rejects(() => client.exec(slow, {sql: "WAITFOR DELAY '00:00:05'"}),
				(e) => e.code === codes.TIMED_OUT);
		}
		finally
		{
			await client.close(slow);
		}
	});
});
