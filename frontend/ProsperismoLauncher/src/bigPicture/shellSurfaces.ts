import type {GameInstall, LibrarySortField, SortDirection} from '../core/models';

/**
 * Recovered NPXS40071 installed-library geometry. These values are copied
 * from SharpEmu's firmware-backed ShellLibraryGrid rather than fitted to a
 * screenshot.
 */
export const SHELL_LIBRARY_METRICS = {
  tileWidth: 296,
  tileHeight: 296,
  columns: 5,
  visibleRows: 3,
  activeAreaOffset: 592,
  gridItemMargin: 20,
  bottomMargin: 90,
  sectionHeaderHeight: 34,
  sectionHeaderBottomMargin: 24,
  sectionTailMargin: 64,
  containerWidth: 1576,
  containerLeft: 172,
  optionContainerLeft: -120,
  sortIconSize: 72,
  sortOptionHeight: 72,
  sortOptionMinWidth: 384,
  paddingVertical: 24,
  paddingHorizontal: 24,
  tilePrimaryPadding: 16,
  tileSecondaryPadding: 8,
  tileAttributeHeight: 32,
  fallbackIconSize: 64,
  gradientOpacityRest: 0.010000001,
  gradientOpacityFocus: 1,
  gradientOffset: -80,
  tileFadeMs: 300,
  loadingPulseMs: 750,
  emptyHeight: 824,
  emptyInnerWidth: 1040,
  emptyButtonMinWidth: 334,
  emptyButtonMaxWidth: 638,
} as const;

export const SHELL_SETTINGS_METRICS = {
  listTop: 186,
  listLeft: 304,
  listWidth: 1312,
  listHeight: 894,
  capturedRowPitch: 102,
  focusMargin: 3,
  separatorMargin: 16,
  separatorHeight: 2,
  iconSize: 64,
  imageMarginRight: 20,
  titleMarginLeft: 16,
  titleMarginRight: 48,
  tabTop: 186,
  tabLeft: 172,
  tabWidth: 388,
  tabPanelLeft: 96,
  tabPanelWidth: 1092,
  tabPanelHeight: 894,
  capturedTabPitch: 110,
  detailRowPitch: 112,
  sectionItemMargin: 14,
  sectionTailMargin: 96,
  indentWidth: 64,
  longTextValueOpacity: 0.7,
} as const;

export const SHELL_OVERLAY_METRICS = {
  functionPanel: {
    anchorX: 1188,
    anchorY: 126,
    width: 652,
    minHeight: 216,
    maxHeight: 810,
    radius: 16,
    headerHeight: 80,
    headerPadding: 24,
    headerOpacity: 0.7,
    iconSize: 48,
    rowHeight: 98,
  },
  functionRow: {
    itemSize: 106,
    focusedSize: 168,
    rowHeight: 168,
    captionHeight: 40,
    itemMargin: 8,
    focusedMargin: 16,
    radius: 16,
    focusedRadius: 168 * (16 / 106),
    itemRevealStaggerMs: 60,
  },
  hubNav: {
    horizontalMarginLeft: 148,
    horizontalMarginRight: 172,
    horizontalWrapperPaddingTop: 40,
    verticalTrackWidth: 2152,
    verticalMarginTop: 86,
    verticalMarginLeft: 40,
    verticalWrapperMarginLeft: -12,
  },
  sceneList: {containerMarginTop: -40, headingGap: 16, sceneGap: 48, tileGap: 32},
  spaceHost: {pitch: 1920},
  marquee: {dwellMs: 2000, frameMs: 16.6667, velocity: 1, fadeOutMs: 300, fadeInMs: 250},
  contextMenu: {
    minWidth: 652,
    maxWidth: 784,
    rowHeight: 98,
    iconGutter: 72,
    iconSize: 40,
    separatorHeight: 2,
    separatorInset: 16,
    separatorTopMargin: 16,
    sectionHeaderHeight: 40,
  },
  dialog: {
    designWidth: 1920,
    designHeight: 1080,
    bodyWidth: 1312,
    sideMargin: 304,
    top: 170,
    fullScreenBodyHeight: 696,
    popupBodyHeight: 594,
    buttonRowGap: 54,
    buttonWidth: 384,
    buttonHeight: 72,
    buttonGap: 16,
    headerHeight: 64,
    borderRadius: 16,
  },
  utility: {
    maxWidth: 416,
    marginTop: 8,
    iconSize: 56,
    iconMarginLeft: 48,
    iconPitch: 104,
    labelTop: 56,
    labelWidth: 336,
    labelMarginTop: 16,
    unfocusedOpacity: 0.6,
  },
  motion: {
    screenMs: 300,
    modalShowMs: 250,
    modalShowDelayMs: 50,
    modalHideMs: 300,
    itemStaggerMs: 1000 / 60,
    riseDistance: 10,
  },
} as const;

export const SHELL_LIBRARY_SORT_OPTIONS = [
  {field: 'gamePath', direction: 'descending', label: 'Installed Date (Newest First)'},
  {field: 'gamePath', direction: 'ascending', label: 'Installed Date (Oldest First)'},
  {field: 'titleName', direction: 'ascending', label: 'Name (A - Z)'},
  {field: 'titleName', direction: 'descending', label: 'Name (Z - A)'},
] satisfies readonly {field: LibrarySortField; direction: SortDirection; label: string}[];

export type ShellLibraryDirection = 'left' | 'right' | 'up' | 'down';

export function shellLibraryMoveIndex(index: number, count: number, direction: ShellLibraryDirection): number {
  if (count <= 0) {
    return -1;
  }
  const current = Math.max(0, Math.min(count - 1, index));
  const columns = SHELL_LIBRARY_METRICS.columns;
  let next = direction === 'left' ? current - 1
    : direction === 'right' ? current + 1
      : direction === 'up' ? current - columns
        : current + columns;
  if (direction === 'down' && next >= count && current < count - 1) {
    next = count - 1;
  }
  return next < 0 || next >= count ? current : next;
}

export function shellLibraryColumnLeft(index: number): number {
  return (index % SHELL_LIBRARY_METRICS.columns)
    * (SHELL_LIBRARY_METRICS.tileWidth + SHELL_LIBRARY_METRICS.paddingHorizontal);
}

export function shellLibraryRowTop(index: number): number {
  const firstRowTop = SHELL_LIBRARY_METRICS.paddingVertical
    + SHELL_LIBRARY_METRICS.sectionHeaderHeight
    + SHELL_LIBRARY_METRICS.sectionHeaderBottomMargin;
  return firstRowTop + Math.floor(index / SHELL_LIBRARY_METRICS.columns)
    * (SHELL_LIBRARY_METRICS.tileHeight + SHELL_LIBRARY_METRICS.paddingVertical);
}

export function shellLibraryContentHeight(count: number): number {
  const rows = count <= 0 ? 0 : Math.ceil(count / SHELL_LIBRARY_METRICS.columns);
  const firstRowTop = shellLibraryRowTop(0);
  const sectionHeight = rows === 0 ? firstRowTop
    : firstRowTop + rows * (SHELL_LIBRARY_METRICS.tileHeight + SHELL_LIBRARY_METRICS.paddingVertical)
      - SHELL_LIBRARY_METRICS.paddingVertical;
  return sectionHeight + SHELL_LIBRARY_METRICS.bottomMargin;
}

export function shellLibraryScrollFor(index: number, count: number, viewportHeight: number): number {
  if (index < 0 || count <= 0) {
    return 0;
  }
  const maxScroll = Math.max(0, shellLibraryContentHeight(count) - viewportHeight);
  const wanted = shellLibraryRowTop(index) - SHELL_LIBRARY_METRICS.activeAreaOffset;
  return Math.max(0, Math.min(maxScroll, wanted));
}

export function sortShellGames(
  games: readonly GameInstall[],
  field: LibrarySortField,
  direction: SortDirection,
): GameInstall[] {
  const multiplier = direction === 'ascending' ? 1 : -1;
  const valueFor = (game: GameInstall): string => {
    switch (field) {
      case 'titleId': return game.titleId;
      case 'gameVersion': return game.gameVersion;
      case 'firmwareVersion': return game.firmwareVersion;
      case 'gamePath': return game.gamePath;
      case 'status': return '';
      case 'comment': return '';
      default: return game.titleName;
    }
  };
  return [...games].sort((left, right) => {
    const a = valueFor(left);
    const b = valueFor(right);
    return a.localeCompare(b, undefined, {numeric: true, sensitivity: 'base'}) * multiplier;
  });
}

/** Exact pure ease-out family used by modal reveal: EaseOutBlast(0, 1). */
export function shellModalShowEase(t: number): number {
  const x = Math.max(0, Math.min(1, t)) * 0.5;
  return 1 - Math.pow(1 - x, 10);
}

/** Exact EaseOutBreeze(0, .4) used for shell surface movement. */
export function shellSurfaceEase(t: number): number {
  const x = Math.max(0, Math.min(1, t)) * (0.8 / (0.6 * 0.4 + 0.2) * 0.5);
  return 1 - Math.pow(1 - x, 4.6);
}

export function shellUtilityWidth(count: number): number {
  return Math.min(
    SHELL_OVERLAY_METRICS.utility.maxWidth,
    Math.max(0, count) * SHELL_OVERLAY_METRICS.utility.iconPitch,
  );
}

export function shellDialogButtonRowWidth(count: number): number {
  if (count <= 0) {
    return 0;
  }
  const {buttonWidth, buttonGap} = SHELL_OVERLAY_METRICS.dialog;
  return count * buttonWidth + (count - 1) * buttonGap;
}
