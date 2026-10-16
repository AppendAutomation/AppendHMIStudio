// The only code in the product that talks to a database.
//
// Two kinds of database are supported. SQL Server goes through tedious, which
// is plain JavaScript over TCP, imported lazily so a project that never uses
// it pays nothing. SQLite goes through node:sqlite, built into Node, so there
// is no native module to build or ship.
//
// Everything here answers in the result codes of ResultCodes.js: a failure
// throws an SqlError carrying the code a script will see, with the database's
// own wording in the message where there is any.
//
// Values handed back are the four kinds a tag can hold -- number, boolean,
// string, null. A date becomes a local-time string, an integer too large for a
// JavaScript number becomes a string so no digits are lost (the same rule the
// comms protocol uses), and binary becomes hex.

import fs from 'fs';
import path from 'path';
import * as codes from './ResultCodes.js';
import { SqlError } from './ResultCodes.js';
import * as secret from './Secret.js';

export const PROVIDERS = ['mssql', 'sqlite'];

export const DEFAULTS = {
	port: 1433,
	maxRows: 10000,
	timeoutMs: 15000
};

export const LIMITS = {
	maxRows: 100000,
	sqlLength: 1000000,
	params: 200,
	nameLength: 64,
	textLength: 128
};

// How a parameter's declared type reaches each provider. The names are the
// ones the SQLSetParam* functions use.
export const PARAM_TYPES = ['char', 'varchar', 'int', 'smallint', 'long', 'float',
	'double', 'decimal', 'date', 'datetime', 'time', 'null'];

function isPlainObject(v)
{
	return v != null && typeof v === 'object' && !Array.isArray(v);
}

function text(value, what, max)
{
	const s = (value == null) ? '' : String(value);

	if (s.length > (max || LIMITS.textLength))
	{
		throw new Error('bad arg: ' + what);
	}

	return s;
}

/**
 * Checks and normalizes a data source. The password is left as it arrives,
 * wrapped or not; open() unwraps it.
 */
export function checkSpec(spec)
{
	if (!isPlainObject(spec))
	{
		throw new Error('bad arg: dataSource');
	}

	const provider = String(spec.provider || '').toLowerCase();

	if (PROVIDERS.indexOf(provider) < 0)
	{
		throw new SqlError(codes.PROVIDER_UNAVAILABLE,
			'"' + provider + '" is not a kind of database this application supports.');
	}

	const options = isPlainObject(spec.options) ? spec.options : {};
	const timeoutMs = (spec.timeoutMs == null) ? DEFAULTS.timeoutMs : Number(spec.timeoutMs);

	if (!Number.isFinite(timeoutMs) || timeoutMs < 100 || timeoutMs > 600000)
	{
		throw new Error('bad arg: timeoutMs');
	}

	const out = {
		name: text(spec.name, 'name', LIMITS.nameLength),
		provider: provider,
		timeoutMs: timeoutMs
	};

	if (provider === 'sqlite')
	{
		out.file = text(spec.file, 'file', 1024);
		out.folder = (String(spec.folder || '').toLowerCase() === 'documents') ? 'documents' : 'appdata';

		if (out.file === '')
		{
			throw new Error('bad arg: file');
		}
	}
	else
	{
		out.host = text(spec.host, 'host', 255);
		out.database = text(spec.database, 'database', 255);
		out.user = text(spec.user, 'user', 128);
		out.password = text(spec.password, 'password', 1024);

		if (out.host === '')
		{
			throw new Error('bad arg: host');
		}

		const port = (spec.port == null || spec.port === '') ? DEFAULTS.port : Number(spec.port);

		if (!Number.isInteger(port) || port < 1 || port > 65535)
		{
			throw new Error('bad arg: port');
		}

		out.port = port;
		// encrypt is off by default: a plant SQL Server is often old enough to
		// offer only TLS versions current OpenSSL refuses, and the traffic is
		// on the plant network. Both switches are per data source.
		out.options = {
			encrypt: options.encrypt === true,
			trustServerCertificate: options.trustServerCertificate !== false,
			legacyTls: options.legacyTls === true,
			instanceName: text(options.instanceName, 'instanceName', 128)
		};
	}

	return out;
}

/**
 * Where a SQLite file may live: the application data folder, or the user's
 * Documents. A project names a file, not a path the renderer chose, so there
 * is no OS dialog to bless it -- these two roots take the place of that.
 *
 * The project holds a name and which of the two folders it belongs to. An
 * absolute path would name one machine's account, and a published application
 * runs on another PC, often as another user; one is still accepted from a
 * hand-written command-line spec, as long as it is inside one of the roots.
 */
export function resolveFile(file, env, folder)
{
	const appData = (env != null && env.userData) ? path.resolve(env.userData, 'databases') : null;
	const documents = (env != null && env.documents) ? path.resolve(env.documents) : null;
	const roots = [appData, documents].filter((r) => r != null);

	if (roots.length === 0)
	{
		throw new SqlError(codes.FILE_NOT_ALLOWED, codes.MESSAGES[codes.FILE_NOT_ALLOWED]);
	}

	const chosen = (folder === 'documents') ? documents : appData;

	if (!path.isAbsolute(file) && chosen == null)
	{
		throw new SqlError(codes.FILE_NOT_ALLOWED, (folder === 'documents') ?
			'The Documents folder cannot be found on this computer.' :
			codes.MESSAGES[codes.FILE_NOT_ALLOWED]);
	}

	const full = path.isAbsolute(file) ? path.normalize(file) : path.resolve(chosen, file);
	const inside = roots.some((root) => full === root || full.startsWith(root + path.sep));

	if (!inside || full.split(path.sep).indexOf('..') >= 0)
	{
		throw new SqlError(codes.FILE_NOT_ALLOWED,
			'A database file must be in Documents or the application data folder.');
	}

	return full;
}

/** A local-time string for a date, with milliseconds only when it has any. */
export function dateText(d)
{
	const pad = (n, w) => String(n).padStart(w || 2, '0');
	const base = d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()) +
		' ' + pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds());

	return (d.getMilliseconds() === 0) ? base : base + '.' + pad(d.getMilliseconds(), 3);
}

/** A database value as a tag can hold it. */
export function cellValue(v)
{
	if (v == null)
	{
		return null;
	}

	if (typeof v === 'number' || typeof v === 'boolean' || typeof v === 'string')
	{
		return v;
	}

	if (typeof v === 'bigint')
	{
		return (v >= BigInt(Number.MIN_SAFE_INTEGER) && v <= BigInt(Number.MAX_SAFE_INTEGER)) ?
			Number(v) : v.toString();
	}

	if (v instanceof Date)
	{
		return Number.isNaN(v.getTime()) ? null : dateText(v);
	}

	if (v instanceof Uint8Array)
	{
		return Buffer.from(v).toString('hex');
	}

	return String(v);
}

/**
 * Rewrites the ? placeholders of a statement into the @p1, @p2 ... that SQL
 * Server wants, skipping anything inside a string, a quoted identifier, a
 * bracketed name or a comment. SQLite takes ? as it is.
 */
export function bindPlaceholders(sql, provider)
{
	if (provider !== 'mssql')
	{
		return {sql: sql, names: null};
	}

	let out = '';
	let i = 0;
	let n = 0;
	const names = [];

	while (i < sql.length)
	{
		const c = sql[i];

		if (c === "'" || c === '"')
		{
			const quote = c;
			let j = i + 1;

			while (j < sql.length)
			{
				if (sql[j] === quote)
				{
					if (sql[j + 1] === quote)
					{
						j += 2;

						continue;
					}

					break;
				}

				j++;
			}

			out += sql.slice(i, Math.min(j + 1, sql.length));
			i = j + 1;
		}
		else if (c === '[')
		{
			const j = sql.indexOf(']', i + 1);
			const end = (j < 0) ? sql.length : j + 1;
			out += sql.slice(i, end);
			i = end;
		}
		else if (c === '-' && sql[i + 1] === '-')
		{
			const j = sql.indexOf('\n', i);
			const end = (j < 0) ? sql.length : j;
			out += sql.slice(i, end);
			i = end;
		}
		else if (c === '/' && sql[i + 1] === '*')
		{
			const j = sql.indexOf('*/', i + 2);
			const end = (j < 0) ? sql.length : j + 2;
			out += sql.slice(i, end);
			i = end;
		}
		else if (c === '?')
		{
			n++;
			const name = 'p' + n;
			names.push(name);
			out += '@' + name;
			i++;
		}
		else
		{
			out += c;
			i++;
		}
	}

	return {sql: out, names: names};
}

function checkRequest(req)
{
	if (!isPlainObject(req))
	{
		throw new Error('bad arg: request');
	}

	const sql = String(req.sql == null ? '' : req.sql);

	if (sql.length > LIMITS.sqlLength)
	{
		throw new Error('bad arg: sql');
	}

	if (sql.trim() === '')
	{
		throw new SqlError(codes.EMPTY_STATEMENT, codes.MESSAGES[codes.EMPTY_STATEMENT]);
	}

	const params = (req.params == null) ? [] : req.params;

	if (!Array.isArray(params) || params.length > LIMITS.params)
	{
		throw new Error('bad arg: params');
	}

	for (const p of params)
	{
		if (!isPlainObject(p) || PARAM_TYPES.indexOf(String(p.type)) < 0)
		{
			throw new Error('bad arg: params');
		}
	}

	let maxRows = (req.maxRows == null) ? DEFAULTS.maxRows : Number(req.maxRows);

	if (!Number.isInteger(maxRows) || maxRows < 1 || maxRows > LIMITS.maxRows)
	{
		throw new Error('bad arg: maxRows');
	}

	return {sql: sql, params: params, maxRows: maxRows};
}

// ---------------------------------------------------------------- SQL Server

let tedious = null;

async function loadTedious()
{
	if (tedious == null)
	{
		try
		{
			tedious = await import('tedious');
		}
		catch (e)
		{
			throw new SqlError(codes.PROVIDER_UNAVAILABLE,
				'Support for SQL Server is not installed with this application.');
		}
	}

	return tedious;
}

/** The first useful sentence out of a driver error, aggregate or not. */
function driverMessage(e)
{
	if (e == null)
	{
		return 'The database reported an error.';
	}

	if (Array.isArray(e.errors) && e.errors.length > 0)
	{
		return e.errors.map((x) => x.message).filter((m) => m).join('; ');
	}

	return e.message || String(e);
}

/**
 * @param phase 'connect' or 'request'. The driver reports a timeout the same
 *              way for both, but they mean different things to whoever reads
 *              the message: not getting there at all, or a statement taking
 *              too long. Which one it was is known only from where it happened.
 */
function mssqlError(e, phase)
{
	const message = driverMessage(e);
	const code = e != null ? e.code : null;

	if (code === 'ETIMEOUT' || code === 'ERequestTimeout')
	{
		return (phase === 'connect') ?
			new SqlError(codes.CONNECT_FAILED, message) : new SqlError(codes.TIMED_OUT, message);
	}

	if (code === 'ESOCKET' || code === 'ECONNREFUSED' || code === 'EHOSTUNREACH' || code === 'ENOTFOUND')
	{
		// A refused TLS handshake reads as an OpenSSL string, which tells an
		// engineer nothing: say what to do about it.
		const tls = /unsupported protocol|wrong version number|sslv3|handshake/i.test(message) ?
			' The server offers an older TLS version than this computer accepts by default:' +
			' turn encryption off for this data source, or turn on its legacy TLS option.' : '';

		return new SqlError(codes.CONNECT_FAILED, message + tls);
	}

	if (code === 'ELOGIN')
	{
		return new SqlError(codes.CONNECT_FAILED, message);
	}

	if (e != null && (e.number === 4060 || e.number === 911))
	{
		return new SqlError(codes.NO_DATABASE, message);
	}

	return new SqlError(codes.PROVIDER_ERROR, message);
}

async function mssqlOpen(spec)
{
	const {Connection} = await loadTedious();

	const options = {
		port: spec.port,
		database: spec.database || undefined,
		encrypt: spec.options.encrypt,
		trustServerCertificate: spec.options.trustServerCertificate,
		instanceName: spec.options.instanceName || undefined,
		// Plant-local time round trips as itself: a time written by a script
		// reads back as the same time, not shifted into UTC.
		useUTC: false,
		requestTimeout: spec.timeoutMs,
		connectTimeout: spec.timeoutMs,
		cancelTimeout: 5000,
		rowCollectionOnRequestCompletion: false
	};

	if (spec.options.legacyTls)
	{
		options.cryptoCredentialsDetails = {minVersion: 'TLSv1', ciphers: 'DEFAULT@SECLEVEL=0'};
	}

	const connection = new Connection({
		server: spec.host,
		authentication: {type: 'default',
			options: {userName: spec.user || undefined, password: secret.unwrap(spec.password) || undefined}},
		options: options
	});

	await new Promise((resolve, reject) =>
	{
		connection.on('connect', (e) => e ? reject(mssqlError(e, 'connect')) : resolve());
		// Without a handler a socket error after connecting takes the process
		// down; failures surface on the request instead.
		connection.on('error', () => {});
		connection.connect();
	});

	return {provider: 'mssql', spec: spec, connection: connection, inTransaction: false};
}

function mssqlParamType(types, type)
{
	switch (type)
	{
		case 'char':
		case 'varchar':
			return types.NVarChar;
		case 'int':
			return types.Int;
		case 'smallint':
			return types.SmallInt;
		case 'long':
			return types.BigInt;
		case 'float':
		case 'double':
			return types.Float;
		case 'decimal':
			return types.Decimal;
		case 'date':
			return types.Date;
		case 'datetime':
			return types.DateTime;
		case 'time':
			return types.Time;
		default:
			return types.NVarChar;
	}
}

function mssqlParamValue(type, value)
{
	if (value == null)
	{
		return null;
	}

	if (type === 'long')
	{
		// tedious wants a string for bigint, so the digits survive
		return String(typeof value === 'number' ? Math.trunc(value) : value);
	}

	if (type === 'date' || type === 'datetime' || type === 'time')
	{
		const d = (value instanceof Date) ? value : new Date(String(value).replace(' ', 'T'));

		if (Number.isNaN(d.getTime()))
		{
			throw new SqlError(codes.INVALID_DATETIME, '"' + value + '" is not a valid date or time.');
		}

		return d;
	}

	if (type === 'int' || type === 'smallint' || type === 'float' || type === 'double')
	{
		const n = Number(value);

		if (!Number.isFinite(n))
		{
			throw new SqlError(codes.PROVIDER_ERROR, '"' + value + '" is not a number.');
		}

		return (type === 'int' || type === 'smallint') ? Math.round(n) : n;
	}

	if (type === 'decimal')
	{
		const n = Number(value);

		if (!Number.isFinite(n))
		{
			throw new SqlError(codes.INVALID_DECIMAL, '"' + value + '" is not a decimal number.');
		}

		return n;
	}

	return String(value);
}

async function mssqlExec(conn, req)
{
	const {Request, TYPES} = await loadTedious();
	const bound = bindPlaceholders(req.sql, 'mssql');
	const rows = [];
	let columns = null;
	let truncated = false;

	const result = await new Promise((resolve, reject) =>
	{
		const request = new Request(bound.sql, (e, rowCount) =>
			e ? reject(mssqlError(e, 'request')) : resolve({rowsAffected: rowCount || 0}));

		request.on('columnMetadata', (meta) =>
		{
			columns = meta.map((m) => ({name: m.colName, type: m.type != null ? m.type.name : null}));
		});

		request.on('row', (row) =>
		{
			if (rows.length >= req.maxRows)
			{
				truncated = true;

				return;
			}

			rows.push(row.map((c) => cellValue(c.value)));
		});

		for (let i = 0; i < req.params.length; i++)
		{
			const p = req.params[i];
			const name = (bound.names != null && bound.names[i] != null) ? bound.names[i] : 'p' + (i + 1);

			if (p.type === 'null')
			{
				request.addParameter(name, mssqlParamType(TYPES, p.nullType || 'varchar'), null);
			}
			else
			{
				request.addParameter(name, mssqlParamType(TYPES, p.type), mssqlParamValue(p.type, p.value));
			}
		}

		conn.connection.execSql(request);
	});

	return {columns: columns || [], rows: rows, rowsAffected: result.rowsAffected, truncated: truncated};
}

function mssqlStatement(conn, sql)
{
	return new Promise((resolve, reject) =>
	{
		const run = (e) => e ? reject(mssqlError(e, 'request')) : resolve();

		if (sql === 'begin')
		{
			conn.connection.beginTransaction(run);
		}
		else if (sql === 'commit')
		{
			conn.connection.commitTransaction(run);
		}
		else
		{
			conn.connection.rollbackTransaction(run);
		}
	});
}

// -------------------------------------------------------------------- SQLite

async function sqliteOpen(spec, env)
{
	const file = resolveFile(spec.file, env, spec.folder);

	let DatabaseSync;

	try
	{
		({DatabaseSync} = await import('node:sqlite'));
	}
	catch (e)
	{
		throw new SqlError(codes.PROVIDER_UNAVAILABLE, 'Support for SQLite is not available in this build.');
	}

	try
	{
		await fs.promises.mkdir(path.dirname(file), {recursive: true});

		const db = new DatabaseSync(file);
		db.exec('PRAGMA busy_timeout = ' + Math.round(spec.timeoutMs));

		return {provider: 'sqlite', spec: spec, db: db, file: file, inTransaction: false};
	}
	catch (e)
	{
		throw new SqlError(codes.CONNECT_FAILED, e.message || String(e));
	}
}

function sqliteError(e)
{
	const message = (e != null && e.message) ? e.message : String(e);

	if (/unable to open database|not a database|no such file/i.test(message))
	{
		return new SqlError(codes.CONNECT_FAILED, message);
	}

	return new SqlError(codes.PROVIDER_ERROR, message);
}

function sqliteValue(type, value)
{
	if (value == null)
	{
		return null;
	}

	if (type === 'int' || type === 'smallint' || type === 'long')
	{
		return Math.round(Number(value));
	}

	if (type === 'float' || type === 'double' || type === 'decimal')
	{
		const n = Number(value);

		if (!Number.isFinite(n))
		{
			throw new SqlError(codes.INVALID_DECIMAL, '"' + value + '" is not a number.');
		}

		return n;
	}

	if (value instanceof Date)
	{
		return dateText(value);
	}

	return (typeof value === 'number') ? value : String(value);
}

function sqliteExec(conn, req)
{
	let statement;

	try
	{
		statement = conn.db.prepare(req.sql);
	}
	catch (e)
	{
		throw sqliteError(e);
	}

	const values = req.params.map((p) => sqliteValue(p.type === 'null' ? 'varchar' : p.type,
		p.type === 'null' ? null : p.value));

	try
	{
		const meta = statement.columns();

		if (meta.length === 0)
		{
			const info = statement.run(...values);

			return {columns: [], rows: [], rowsAffected: Number(info.changes) || 0, truncated: false};
		}

		statement.setReadBigInts(true);

		const columns = meta.map((m) => ({name: m.name, type: m.type || null}));
		const all = statement.all(...values);
		const truncated = all.length > req.maxRows;
		const rows = (truncated ? all.slice(0, req.maxRows) : all)
			.map((row) => columns.map((c) => cellValue(row[c.name])));

		return {columns: columns, rows: rows, rowsAffected: rows.length, truncated: truncated};
	}
	catch (e)
	{
		throw sqliteError(e);
	}
}

// ----------------------------------------------------------------- public API

/** Opens a connection. env gives the folders a SQLite file may live in. */
export async function open(spec, env)
{
	const clean = checkSpec(spec);

	return (clean.provider === 'sqlite') ? sqliteOpen(clean, env) : mssqlOpen(clean);
}

/** Runs one statement. */
export async function exec(conn, request)
{
	if (conn == null)
	{
		throw new SqlError(codes.INVALID_CONNECTION, codes.MESSAGES[codes.INVALID_CONNECTION]);
	}

	const req = checkRequest(request);

	return (conn.provider === 'sqlite') ? sqliteExec(conn, req) : mssqlExec(conn, req);
}

export async function close(conn)
{
	if (conn == null)
	{
		return;
	}

	try
	{
		if (conn.provider === 'sqlite')
		{
			conn.db.close();
		}
		else
		{
			conn.connection.close();
		}
	}
	catch (e)
	{
		// Closing is best effort: the caller is finished with it either way
	}
}

export async function begin(conn)
{
	if (conn.inTransaction)
	{
		throw new SqlError(codes.TRANSACTION_STATE, 'A transaction is already open on this connection.');
	}

	if (conn.provider === 'sqlite')
	{
		try
		{
			conn.db.exec('BEGIN');
		}
		catch (e)
		{
			throw sqliteError(e);
		}
	}
	else
	{
		await mssqlStatement(conn, 'begin');
	}

	conn.inTransaction = true;
}

export async function commit(conn)
{
	if (!conn.inTransaction)
	{
		throw new SqlError(codes.TRANSACTION_STATE, 'No transaction is open on this connection.');
	}

	if (conn.provider === 'sqlite')
	{
		try
		{
			conn.db.exec('COMMIT');
		}
		catch (e)
		{
			throw sqliteError(e);
		}
	}
	else
	{
		await mssqlStatement(conn, 'commit');
	}

	conn.inTransaction = false;
}

export async function rollback(conn)
{
	if (!conn.inTransaction)
	{
		throw new SqlError(codes.TRANSACTION_STATE, 'No transaction is open on this connection.');
	}

	if (conn.provider === 'sqlite')
	{
		try
		{
			conn.db.exec('ROLLBACK');
		}
		catch (e)
		{
			throw sqliteError(e);
		}
	}
	else
	{
		await mssqlStatement(conn, 'rollback');
	}

	conn.inTransaction = false;
}

/** Tries a data source and lets go of it again: the dialog's Test button. */
export async function probe(spec, env)
{
	const started = Date.now();
	let conn = null;

	try
	{
		conn = await open(spec, env);
		const res = await exec(conn, {sql: (conn.provider === 'sqlite') ?
			'SELECT sqlite_version()' : 'SELECT @@VERSION'});
		const version = (res.rows.length > 0 && res.rows[0].length > 0) ?
			String(res.rows[0][0]).split('\n')[0].trim() : '';

		return {ok: true, ms: Date.now() - started, version: version};
	}
	catch (e)
	{
		return {ok: false, ms: Date.now() - started, code: codes.codeOf(e), error: e.message};
	}
	finally
	{
		await close(conn);
	}
}
