import {defineConfig} from 'vite';
import react from '@vitejs/plugin-react';
import {fileURLToPath} from 'node:url';

const appRoot = fileURLToPath(new URL('..', import.meta.url));

/**
 * Browser preview of the Big Picture shell.
 *
 * This exists because there is no react-native-macos for the 0.83 line (the
 * newest release is 0.81.9, which peer-pins react-native to exactly 0.81.6),
 * so the shell cannot be previewed natively on macOS. react-native-web needs
 * no Xcode and no CocoaPods.
 *
 * It is a LAYOUT AND MOTION preview only. The five Windows natives resolve to
 * null here and the shell falls back, so the FirstWave background, the UI3
 * focus treatment, firmware icons and the native font resolver are all absent.
 * Never treat a screenshot from this harness as a fidelity capture.
 *
 * Nothing here participates in the Windows build: it is confined to web/ and
 * to devDependencies.
 */
export default defineConfig({
  root: fileURLToPath(new URL('.', import.meta.url)),
  resolve: {
    alias: {
      'react-native': 'react-native-web',
    },
    extensions: [
      '.web.tsx', '.web.ts', '.web.jsx', '.web.js',
      '.tsx', '.ts', '.jsx', '.js', '.json',
    ],
  },
  define: {
    global: 'globalThis',
    __DEV__: 'true',
    'process.env.NODE_ENV': JSON.stringify('development'),
  },
  optimizeDeps: {
    esbuildOptions: {
      // React Native source ships untranspiled Flow-free JSX in .js files.
      loader: {'.js': 'jsx'},
    },
  },
  server: {fs: {allow: [appRoot]}, port: 5273},
});
