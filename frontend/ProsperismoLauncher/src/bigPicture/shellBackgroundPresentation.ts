import type {ShellSurface} from './shellState';

/**
 * The native shell owns one persistent FirstWave plate.  HOME additionally
 * exposes the recovered particle pass; utility surfaces select NoParticle.
 * Keeping this decision outside the renderer prevents a route transition from
 * destroying and recreating the native drawing surface.
 */
export interface ShellBackgroundPresentation {
  particleOverlayEnabled: boolean;
  state: 'home' | 'no-particle';
}

export function shellBackgroundPresentation(
  surface: ShellSurface,
  modalOpen: boolean,
): ShellBackgroundPresentation {
  const home = surface === 'home' && !modalOpen;
  return {
    particleOverlayEnabled: home,
    state: home ? 'home' : 'no-particle',
  };
}
