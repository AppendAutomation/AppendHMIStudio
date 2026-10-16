// One window's database connections.
//
// The renderer never reaches a database itself -- the page's CSP forbids it --
// so main holds the connections and the window addresses them by the number
// SQLConnect() gave it. Numbering starts at 1 and counts up for the life of
// the window, as the script function family expects.
//
// Methods answer in result codes rather than throwing, because that is what a
// script sees; a malformed request from the renderer is a different matter and
// does throw, like the comms session's checks.

import * as client from './SqlClient.js';
import * as store from './DataSourceStore.js';
import * as codes from './ResultCodes.js';

export const MAX_CONNECTIONS = 8;

function isPlainObject(v)
{
	return v != null && typeof v === 'object' && !Array.isArray(v);
}

export class SqlSession
{
	/**
	 * @param env  {userData, documents} -- the folders a SQLite file may use,
	 *             and where the per-PC credentials live
	 */
	constructor(env)
	{
		this.env = env || {};
		this.connections = new Map();
		this.nextId = 1;
		this.closed = false;
	}

	/** Opens a data source. The reply carries the number scripts will use. */
	async connect(args)
	{
		if (this.closed)
		{
			return {code: codes.INVALID_CONNECTION, message: codes.message(codes.INVALID_CONNECTION)};
		}

		if (this.connections.size >= MAX_CONNECTIONS)
		{
			return {code: codes.TOO_MANY_CONNECTIONS, message: codes.message(codes.TOO_MANY_CONNECTIONS)};
		}

		try
		{
			let spec = client.checkSpec(args.dataSource);

			if (args.store)
			{
				spec = store.applyOverride(spec, await store.load(this.env.userData, args.store));
			}

			const conn = await client.open(spec, this.env);
			const id = this.nextId++;
			this.connections.set(id, conn);

			return {code: codes.OK, connectionId: id, provider: conn.provider};
		}
		catch (e)
		{
			if (!Number.isFinite(e.code))
			{
				throw e;
			}

			return {code: e.code, message: e.message};
		}
	}

	connectionOf(connectionId)
	{
		const conn = this.connections.get(Number(connectionId));

		return (conn != null) ? conn : null;
	}

	/** Runs one statement on one connection. */
	async exec(args)
	{
		const conn = this.connectionOf(args.connectionId);

		if (conn == null)
		{
			return {code: codes.INVALID_CONNECTION, message: codes.message(codes.INVALID_CONNECTION)};
		}

		try
		{
			const res = await client.exec(conn, {sql: args.sql, params: args.params, maxRows: args.maxRows});

			return {code: codes.OK, columns: res.columns, rows: res.rows,
				rowsAffected: res.rowsAffected, truncated: res.truncated};
		}
		catch (e)
		{
			if (!Number.isFinite(e.code))
			{
				throw e;
			}

			return {code: e.code, message: e.message};
		}
	}

	async transaction(action, args)
	{
		const conn = this.connectionOf(args.connectionId);

		if (conn == null)
		{
			return {code: codes.INVALID_CONNECTION, message: codes.message(codes.INVALID_CONNECTION)};
		}

		try
		{
			await client[action](conn);

			return {code: codes.OK};
		}
		catch (e)
		{
			if (!Number.isFinite(e.code))
			{
				throw e;
			}

			return {code: e.code, message: e.message};
		}
	}

	/** Closes one connection, rolling back anything left open. */
	async close(args)
	{
		const id = Number(args.connectionId);
		const conn = this.connections.get(id);

		if (conn == null)
		{
			return {code: codes.INVALID_CONNECTION, message: codes.message(codes.INVALID_CONNECTION)};
		}

		this.connections.delete(id);
		await this.release(conn);

		return {code: codes.OK};
	}

	async release(conn)
	{
		if (conn.inTransaction)
		{
			try
			{
				await client.rollback(conn);
			}
			catch (e)
			{
				// Going away regardless; the database rolls back an abandoned
				// transaction when the connection drops
			}
		}

		await client.close(conn);
	}

	/** Everything goes when the window does, or when the Run stops. */
	async closeAll()
	{
		this.closed = true;
		const open = Array.from(this.connections.values());
		this.connections.clear();

		for (const conn of open)
		{
			await this.release(conn);
		}
	}
}

/**
 * Checks a request from a window before it reaches the session. A window
 * cannot name a file path, read a stored password, or ask for an unbounded
 * result; anything malformed is a bad argument, not a result code.
 */
export function validateSqlArgs(action, args)
{
	const bad = (what) =>
	{
		throw new Error('bad arg: ' + what);
	};

	switch (action)
	{
		case 'hmiSql.connect':
			if (!isPlainObject(args.dataSource))
			{
				bad('dataSource');
			}

			if (args.store != null && typeof args.store !== 'string')
			{
				bad('store');
			}

			break;

		case 'hmiSql.exec':
			if (typeof args.sql !== 'string')
			{
				bad('sql');
			}

			if (args.params != null && (!Array.isArray(args.params) ||
				args.params.length > client.LIMITS.params || !args.params.every(isPlainObject)))
			{
				bad('params');
			}

			if (args.maxRows != null && !(Number.isInteger(args.maxRows) &&
				args.maxRows >= 1 && args.maxRows <= client.LIMITS.maxRows))
			{
				bad('maxRows');
			}

			// falls through to the connection check

		case 'hmiSql.close':
		case 'hmiSql.begin':
		case 'hmiSql.commit':
		case 'hmiSql.rollback':
			if (!Number.isInteger(args.connectionId) || args.connectionId < 1)
			{
				bad('connectionId');
			}

			break;

		case 'hmiSql.probe':
			if (!isPlainObject(args.dataSource))
			{
				bad('dataSource');
			}

			break;

		case 'hmiSql.overrides':
		case 'hmiSql.clearOverrides':
			if (typeof args.store !== 'string')
			{
				bad('store');
			}

			break;

		case 'hmiSql.setOverride':
			if (typeof args.store !== 'string')
			{
				bad('store');
			}

			if (typeof args.name !== 'string' || args.name === '' || args.name.length > store.MAX_NAME)
			{
				bad('name');
			}

			if (args.override != null && !isPlainObject(args.override))
			{
				bad('override');
			}

			break;

		case 'hmiSql.wrapSecret':
			if (typeof args.password !== 'string' || args.password.length > store.MAX_PASSWORD)
			{
				bad('password');
			}

			break;

		default:
			throw new Error('unknown action: ' + action);
	}
}
