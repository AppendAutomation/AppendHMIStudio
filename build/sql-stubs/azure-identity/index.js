// Azure AD authentication is not offered: a data source signs in with a user
// name and password, or with neither. See ../README.md.
'use strict';

function unavailable(name)
{
	return class
	{
		constructor()
		{
			throw new Error(name + ' is not available: this application does not offer ' +
				'Azure AD authentication for a data source.');
		}
	};
}

module.exports = {
	ClientSecretCredential: unavailable('ClientSecretCredential'),
	DefaultAzureCredential: unavailable('DefaultAzureCredential'),
	ManagedIdentityCredential: unavailable('ManagedIdentityCredential'),
	UsernamePasswordCredential: unavailable('UsernamePasswordCredential')
};
