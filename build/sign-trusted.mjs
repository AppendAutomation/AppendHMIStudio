// electron-builder Windows signing hook for Azure Trusted Signing ("Artifact
// Signing"; the dlib keeps its original name). Signing is opt-in: builds are
// unsigned unless all of these are set, and signtool.exe (Windows) is needed:
//
//   HMI_SIGNING_ACCOUNT     Trusted Signing account name
//   HMI_SIGNING_PROFILE     certificate profile name
//   HMI_SIGNING_ENDPOINT    e.g. https://weu.codesigning.azure.net
//   TRUSTED_SIGNING_DLIB_PATH   Azure.CodeSigning.Dlib.dll (from NuGet)
//   SIGNTOOL_PATH               signtool.exe from the Windows SDK
//
// Authentication uses DefaultAzureCredential: AZURE_TENANT_ID,
// AZURE_CLIENT_ID, AZURE_CLIENT_SECRET.

import { execFileSync } from 'child_process';
import path from 'path';
import fs from 'fs';
import os from 'os';

export default async function (configuration)
{
    const account = process.env.HMI_SIGNING_ACCOUNT;
    const profile = process.env.HMI_SIGNING_PROFILE;
    const endpoint = process.env.HMI_SIGNING_ENDPOINT;

    if (!account || !profile || !endpoint)
    {
        console.log(`Not signing ${path.basename(configuration.path)} (no HMI_SIGNING_* configuration)`);
        return;
    }

    const dlibPath = process.env.TRUSTED_SIGNING_DLIB_PATH;
    const signtool = process.env.SIGNTOOL_PATH;

    if (!dlibPath || !fs.existsSync(dlibPath))
    {
        throw new Error(`Trusted Signing dlib not found at "${dlibPath}" (TRUSTED_SIGNING_DLIB_PATH).`);
    }

    if (!signtool || !fs.existsSync(signtool))
    {
        throw new Error(`signtool.exe not found at "${signtool}" (SIGNTOOL_PATH).`);
    }

    const metadata = {
        Endpoint: endpoint,
        CodeSigningAccountName: account,
        CertificateProfileName: profile
    };

    const metadataPath = path.join(os.tmpdir(), `trusted-signing-${process.pid}-${Date.now()}.json`);
    fs.writeFileSync(metadataPath, JSON.stringify(metadata));

    try
    {
        const file = configuration.path;
        console.log(`Trusted Signing: ${file}`);

        execFileSync(signtool, [
            'sign',
            '/v',
            '/fd', 'SHA256',
            '/td', 'SHA256',
            '/tr', 'http://timestamp.acs.microsoft.com',
            '/dlib', dlibPath,
            '/dmdf', metadataPath,
            file
        ], { stdio: 'inherit' });
    }
    finally
    {
        try { fs.unlinkSync(metadataPath); } catch (e) { /* ignore */ }
    }
}
