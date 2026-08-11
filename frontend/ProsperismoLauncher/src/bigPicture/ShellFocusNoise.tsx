import React, {createContext, useContext, type PropsWithChildren} from 'react';

const ShellFocusNoiseContext = createContext('');

export function ShellFocusNoiseProvider({
  children,
  path,
}: PropsWithChildren<{path?: string}>) {
  return <ShellFocusNoiseContext.Provider value={path ?? ''}>
    {children}
  </ShellFocusNoiseContext.Provider>;
}

export function useShellFocusNoisePath(): string {
  return useContext(ShellFocusNoiseContext);
}
