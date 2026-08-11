const {getDefaultConfig, mergeConfig} = require('@react-native/metro-config');

const fs = require('fs');
const path = require('node:path');

const rnwPath = fs.realpathSync(
  path.resolve(require.resolve('react-native-windows/package.json'), '..'),
);
const nodeModulesPath = fs.realpathSync(path.resolve(__dirname, 'node_modules'));

//

/**
 * Metro configuration
 * https://facebook.github.io/metro/docs/configuration
 *
 * @type {import('metro-config').MetroConfig}
 */

const config = {
  // A UI worktree can reuse the checked-out dependency cache through a
  // junction. Metro resolves the junction to its real path, so that path must
  // be watched as well or bundle resolution loses @babel/runtime.
  watchFolders: [path.resolve(__dirname, '../../assets/branding'), nodeModulesPath],
  //
  resolver: {
    nodeModulesPaths: [nodeModulesPath, path.resolve(__dirname, 'node_modules')],
    extraNodeModules: {
      'react-native': path.resolve(__dirname, 'node_modules/react-native'),
    },
    blockList: [
      // This stops "npx @react-native-community/cli run-windows" from causing the metro server to crash if its already running
      new RegExp(
        `${path.resolve(__dirname, 'windows').replace(/[/\\]/g, '/')}.*`,
      ),
      // This prevents "npx @react-native-community/cli run-windows" from hitting: EBUSY: resource busy or locked, open msbuild.ProjectImports.zip or other files produced by msbuild
      new RegExp(`${rnwPath}/build/.*`),
      new RegExp(`${rnwPath}/target/.*`),
      /.*\.ProjectImports\.zip/,
    ],
    //
  },
  transformer: {
    getTransformOptions: async () => ({
      transform: {
        experimentalImportSupport: false,
        inlineRequires: true,
      },
    }),
  },
};

module.exports = mergeConfig(getDefaultConfig(__dirname), config);
