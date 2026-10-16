// Azure Key Vault column encryption is not offered. See ../README.md.
'use strict';

function unavailable(name)
{
	return class
	{
		constructor()
		{
			throw new Error(name + ' is not available: this application does not offer ' +
				'Azure Key Vault column encryption.');
		}
	};
}

module.exports = {
	KeyClient: unavailable('KeyClient'),
	CryptographyClient: unavailable('CryptographyClient')
};
