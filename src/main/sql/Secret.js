// Wrapping for a data source password kept in a project or in the per-PC
// override file.
//
// This is obfuscation, not security, and the documentation says so plainly.
// The key is derived from a fixed string in this file, so every copy of the
// product can unwrap what any other wrapped -- which is what makes a project
// portable, and equally what makes the wrapping reversible by anyone who has
// the product. It keeps the password out of plain sight in the project XML, in
// a screenshot and in a text editor. It keeps it from nobody who looks for it.
//
// A site that wants the password not to travel with the project sets it on the
// target PC instead (DataSourceStore.js).

import crypto from 'crypto';

const PREFIX = 'hmi1:';
const KEY_SOURCE = 'Append HMI Studio data source password wrapping';
const IV_BYTES = 12;

function key()
{
	return crypto.createHash('sha256').update(KEY_SOURCE, 'utf8').digest();
}

/** Whether a value is already wrapped. */
export function isWrapped(value)
{
	return typeof value === 'string' && value.startsWith(PREFIX);
}

/** Wraps a password. An empty password wraps to an empty string. */
export function wrap(plain)
{
	const text = (plain == null) ? '' : String(plain);

	if (text === '')
	{
		return '';
	}

	if (isWrapped(text))
	{
		return text;
	}

	const iv = crypto.randomBytes(IV_BYTES);
	const cipher = crypto.createCipheriv('aes-256-gcm', key(), iv);
	const body = Buffer.concat([cipher.update(text, 'utf8'), cipher.final()]);

	return PREFIX + Buffer.concat([iv, cipher.getAuthTag(), body]).toString('base64');
}

/**
 * Unwraps a password. A value that was never wrapped is returned as it is, so
 * a hand-edited project with a plain password still works.
 */
export function unwrap(value)
{
	if (value == null || value === '')
	{
		return '';
	}

	const text = String(value);

	if (!isWrapped(text))
	{
		return text;
	}

	try
	{
		const raw = Buffer.from(text.slice(PREFIX.length), 'base64');
		const iv = raw.subarray(0, IV_BYTES);
		const tag = raw.subarray(IV_BYTES, IV_BYTES + 16);
		const body = raw.subarray(IV_BYTES + 16);
		const decipher = crypto.createDecipheriv('aes-256-gcm', key(), iv);
		decipher.setAuthTag(tag);

		return decipher.update(body, undefined, 'utf8') + decipher.final('utf8');
	}
	catch (e)
	{
		// Damaged or truncated: treat it as no password rather than failing the
		// connection with something the user cannot act on
		return '';
	}
}
