// RNW 0.84 expects PowerShell 7 while this machine only has Windows PowerShell.
// Generation scripts used by init-windows are compatible with Windows PowerShell.
const {execFileSync} = require('node:child_process');
const path = require('node:path');
const finder = require('@react-native-windows/find-dotnet-tools');

// run-windows starts MSBuild, which in turn starts fresh Node processes for
// autolinking and bundling. Keep the shim active across that process boundary;
// `node -r` alone affects only the initial CLI process.
const self = path.resolve(__filename);
if (!(process.env.NODE_OPTIONS || '').includes(self)) {
  const inherited = process.env.NODE_OPTIONS ? `${process.env.NODE_OPTIONS} ` : '';
  process.env.NODE_OPTIONS = `${inherited}--require=${JSON.stringify(self)}`;
}

// RNW 0.83's generated project targets VS 2022/v143. A newer shared tooling
// helper defaults to the next VS release unless the supported floor is stated.
process.env.MinimumVisualStudioVersion ||= '17.0';

finder.findPowerShell = () => {
  try {
    return execFileSync('where.exe', ['pwsh.exe'], {encoding: 'utf8'}).trim().split(/\r?\n/)[0];
  } catch {
    return 'powershell.exe';
  }
};
