const WINDOWS_ROOT = /^[A-Za-z]:[\\/]/;

export function pathSeparator(path: string): '/' | '\\' {
  return WINDOWS_ROOT.test(path) || path.includes('\\') ? '\\' : '/';
}

export function joinPath(base: string, ...parts: string[]): string {
  const separator = pathSeparator(base);
  const all = [base, ...parts]
    .filter(Boolean)
    .map((part, index) =>
      index === 0
        ? part.replace(/[\\/]+$/g, '')
        : part.replace(/^[\\/]+|[\\/]+$/g, ''),
    );
  return all.join(separator);
}

export function baseName(path: string): string {
  const clean = path.replace(/[\\/]+$/g, '');
  return clean.slice(Math.max(clean.lastIndexOf('/'), clean.lastIndexOf('\\')) + 1);
}

export function windowsPathKey(path: string): string {
  return path.replace(/[\\/]+$/g, '').toLocaleLowerCase('en-US');
}
