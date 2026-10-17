// Result codes returned by the SQL script functions, and the message
// SQLErrorMsg() gives for each.
//
// The numbers are an interface: scripts test them, so they are fixed. Codes
// down to -1013 are the ones the function family has always used; -1100 and
// below are ours, for conditions the original set had no code for.
//
// -1007 is deliberately absent. It was the "string is truncated" warning,
// raised when a column was longer than a message tag could hold. Our message
// tags are not length-limited, so nothing truncates and nothing reports it.

export const OK = 0;
export const PROVIDER_ERROR = -1;
export const EMPTY_STATEMENT = -2;
export const NULL_VALUE = -4;
export const NO_MORE_ROWS = -5;
export const INVALID_PARAM_ID = -7;
export const INVALID_PARAM_LIST = -8;
export const INVALID_NULL_TYPE = -9;
export const PARAM_TYPE_CHANGED = -10;
export const PARAM_AFTER_EXECUTE = -11;
export const INVALID_DATETIME = -12;
export const INVALID_DECIMAL = -13;
export const INVALID_CURRENCY = -14;
export const INVALID_STATEMENT_TYPE = -15;
export const OUT_OF_MEMORY = -1001;
export const INVALID_CONNECTION = -1002;
export const NO_BIND_LIST = -1003;
export const NO_TEMPLATE = -1004;
export const INTERNAL = -1005;
export const STRING_NULL = -1006;
export const NO_WHERE_CLAUSE = -1008;
export const CONNECT_FAILED = -1009;
export const NO_DATABASE = -1010;
export const NO_ROWS_SELECTED = -1011;
export const NO_FILE = -1013;

// Ours
export const NO_DATA_SOURCE = -1100;
export const TIMED_OUT = -1101;
export const TOO_MANY_ROWS = -1102;
export const TOO_MANY_CONNECTIONS = -1103;
export const PROVIDER_UNAVAILABLE = -1104;
export const NO_QUERY = -1105;
export const TRANSACTION_STATE = -1106;
export const FILE_NOT_ALLOWED = -1107;
export const ROW_GONE = -1108;
export const NO_STATEMENT = -1109;

export const MESSAGES = {
	[OK]: 'No errors occurred',
	[PROVIDER_ERROR]: 'The database reported an error',
	[EMPTY_STATEMENT]: 'There is no statement to execute',
	[NULL_VALUE]: 'The value read was null',
	[NO_MORE_ROWS]: 'There are no more rows to fetch',
	[INVALID_PARAM_ID]: 'That parameter number is not valid',
	[INVALID_PARAM_LIST]: 'The statement\'s parameters have not all been set',
	[INVALID_NULL_TYPE]: 'That type cannot be used for a null parameter',
	[PARAM_TYPE_CHANGED]: "A parameter's type cannot be changed once it is set",
	[PARAM_AFTER_EXECUTE]: 'A parameter cannot be added after the statement has run',
	[INVALID_DATETIME]: 'That is not a valid date or time',
	[INVALID_DECIMAL]: 'That is not a valid decimal number',
	[INVALID_CURRENCY]: 'That is not a valid currency amount',
	[INVALID_STATEMENT_TYPE]: 'That operation does not suit this kind of statement',
	[OUT_OF_MEMORY]: 'There is not enough memory to complete this operation',
	[INVALID_CONNECTION]: 'That connection number is not open',
	[NO_BIND_LIST]: 'There is no bind list of that name',
	[NO_TEMPLATE]: 'There is no table template of that name',
	[INTERNAL]: 'An internal error occurred',
	[STRING_NULL]: 'The string read was null',
	[NO_WHERE_CLAUSE]: 'Deleting records needs a where expression',
	[CONNECT_FAILED]: 'The database could not be reached',
	[NO_DATABASE]: 'That database does not exist',
	[NO_ROWS_SELECTED]: 'No rows have been selected',
	[NO_FILE]: 'The file could not be found',
	[NO_DATA_SOURCE]: 'There is no data source of that name',
	[TIMED_OUT]: 'The database did not answer in time',
	[TOO_MANY_ROWS]: 'The query returned more rows than the limit allows',
	[TOO_MANY_CONNECTIONS]: 'Too many database connections are open',
	[PROVIDER_UNAVAILABLE]: 'That kind of database is not supported',
	[NO_QUERY]: 'There is no query of that name',
	[TRANSACTION_STATE]: 'That does not suit the current transaction',
	[FILE_NOT_ALLOWED]: 'A database file must be in Documents or the application data folder',
	[ROW_GONE]: 'The row the cursor is on is no longer in the table',
	[NO_STATEMENT]: 'There is no prepared statement of that number'
};

/** The message for a code. A code we do not know still reads sensibly. */
export function message(code)
{
	const n = Number(code);

	if (!Number.isFinite(n))
	{
		return MESSAGES[INTERNAL];
	}

	return MESSAGES[n] != null ? MESSAGES[n] : 'Result code ' + n;
}

/**
 * An error carrying the code a script will see. The database's own wording is
 * kept in the message: PROVIDER_ERROR says only that there was one.
 */
export class SqlError extends Error
{
	constructor(code, message)
	{
		super(message || MESSAGES[code] || 'Result code ' + code);
		this.name = 'SqlError';
		this.code = code;
	}
}

/** The code for a thrown error, for callers that answer in codes. */
export function codeOf(e)
{
	return (e != null && Number.isFinite(e.code)) ? e.code : PROVIDER_ERROR;
}
