import {
  marqueeSpeedCoefficient,
  SHELL_FUNCTION_PANEL,
  SHELL_HUB_NAV,
  SHELL_TILE_CATALOGUE,
  ShellMarqueeCycle,
  ShellSpaceTransition,
  shellFunctionPanelHeight,
} from '../src/bigPicture/shellRecoveredCatalogue';

describe('recovered auxiliary HOME controls', () => {
  it('keeps the function panel at its fixed firmware anchor and clamps height', () => {
    expect(SHELL_FUNCTION_PANEL.anchorX).toBe(1188);
    expect(shellFunctionPanelHeight(0)).toBe(216);
    expect(shellFunctionPanelHeight(20)).toBe(810);
  });

  it('retains the negative hub wrapper margins that cancel the nav insets', () => {
    expect(SHELL_HUB_NAV.horizontalMarginLeft + SHELL_HUB_NAV.horizontalWrapperMarginLeft).toBe(0);
    expect(SHELL_HUB_NAV.horizontalMarginRight + SHELL_HUB_NAV.horizontalWrapperMarginRight).toBe(0);
  });

  it('ports all 21 closed catalogue shapes and keeps SLIM widths fluid', () => {
    expect(SHELL_TILE_CATALOGUE).toHaveLength(21);
    expect(SHELL_TILE_CATALOGUE.find(item => item.name === 'PLAIN.SQUARE.SMALL')?.width).toBe(296);
    expect(SHELL_TILE_CATALOGUE.find(item => item.name === 'SLIM.SQUARE')?.width).toBeUndefined();
  });

  it('runs the UI3 marquee in one direction then fades and snaps', () => {
    const marquee = new ShellMarqueeCycle();
    expect(marqueeSpeedCoefficient('very-slow')).toBe(0.25);
    marquee.advance(2001, 60);
    marquee.advance(1001, 60);
    expect(marquee.status).toBe('stop-at-right');
    marquee.advance(300, 60);
    expect(marquee.offset).toBe(0);
    expect(marquee.opacity).toBe(0);
    marquee.advance(250, 60);
    expect(marquee.opacity).toBe(1);
  });

  it('jumps space pan immediately, hides outgoing, and springs only incoming', () => {
    const spaces = new ShellSpaceTransition(2);
    spaces.select(1);
    expect(spaces.translateX).toBe(-1920);
    expect(spaces.opacities[0].value).toBe(0);
    expect(spaces.opacities[1].value).toBe(0);
    spaces.advance(0.064);
    expect(spaces.opacities[1].value).toBeGreaterThan(0);
  });
});
