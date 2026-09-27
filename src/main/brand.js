// The product's identity, in one place for the main process, the Publisher
// and the build scripts. Packaging repeats it in package.json and the
// electron-builder configs; src/test/brand.test.js keeps them in step.

export const PRODUCT_NAME = 'Append HMI Studio';
export const PUBLISHER = 'Append Automation';
export const APP_ID = 'com.appendautomation.hmistudio';

// The Windows exe (also the Publish template's) and the Linux binary
export const WINDOWS_EXE = PRODUCT_NAME + '.exe';
export const LINUX_EXECUTABLE = 'append-hmi-studio';

export const HOMEPAGE_URL = 'https://github.com/AppendAutomation/AppendHMIStudio';
export const ISSUES_URL = HOMEPAGE_URL + '/issues';
