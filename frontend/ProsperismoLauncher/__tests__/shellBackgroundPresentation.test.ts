import {shellBackgroundPresentation} from '../src/bigPicture/shellBackgroundPresentation';

describe('recovered shell background presentation', () => {
  it('keeps particles only on unobscured HOME', () => {
    expect(shellBackgroundPresentation('home', false)).toEqual({
      particleOverlayEnabled: true,
      state: 'home',
    });
    expect(shellBackgroundPresentation('home', true)).toEqual({
      particleOverlayEnabled: false,
      state: 'no-particle',
    });
  });

  it.each(['library', 'settings'] as const)('keeps the plate but gates particles on %s', surface => {
    expect(shellBackgroundPresentation(surface, false)).toEqual({
      particleOverlayEnabled: false,
      state: 'no-particle',
    });
  });
});
