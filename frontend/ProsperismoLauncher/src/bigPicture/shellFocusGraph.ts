export type ShellFocusDirection = 'left' | 'right' | 'up' | 'down';

export interface ShellFocusRegionSpec {
  name: string;
  itemCount: number;
  lastFocusedItem?: number;
  leftCandidate?: string;
  rightCandidate?: string;
  upCandidate?: string;
  downCandidate?: string;
  canMoveLeft?: boolean;
  canMoveRight?: boolean;
  canMoveUp?: boolean;
  canMoveDown?: boolean;
}

export interface ShellFocusLocation {
  region: string;
  index: number;
}

/**
 * Direct TypeScript translation of SharpEmu's named-region focus graph.
 * Edges clamp unless both the permission and named candidate are present.
 * Re-entering a region restores its last focused item.
 */
export class ShellFocusGraph {
  private readonly regions = new Map<string, ShellFocusRegionSpec>();
  private activeRegion?: string;

  add(spec: ShellFocusRegionSpec): ShellFocusRegionSpec {
    const region = {...spec};
    this.regions.set(region.name, region);
    this.activeRegion ??= region.name;
    return region;
  }

  find(name: string | undefined): ShellFocusRegionSpec | undefined {
    return name ? this.regions.get(name) : undefined;
  }

  active(): ShellFocusLocation | undefined {
    const region = this.find(this.activeRegion);
    if (!region) {
      return undefined;
    }
    return {region: region.name, index: this.clampIndex(region)};
  }

  setItemCount(name: string, count: number): void {
    const region = this.find(name);
    if (!region) {
      return;
    }
    region.itemCount = Math.max(0, count);
    region.lastFocusedItem = this.clampIndex(region);
  }

  remember(name: string, index: number): void {
    const region = this.find(name);
    if (region && index >= 0) {
      region.lastFocusedItem = index;
    }
  }

  setActive(name: string): boolean {
    if (!this.find(name)) {
      return false;
    }
    this.activeRegion = name;
    return true;
  }

  tryMove(direction: ShellFocusDirection): ShellFocusLocation | undefined {
    const current = this.find(this.activeRegion);
    if (!current) {
      return undefined;
    }
    const title = direction[0].toUpperCase() + direction.slice(1);
    const canMove = current[`canMove${title}` as keyof ShellFocusRegionSpec] === true;
    const candidate = current[`${direction}Candidate` as keyof ShellFocusRegionSpec];
    const target = typeof candidate === 'string' ? this.find(candidate) : undefined;
    if (!canMove || !target || target.itemCount <= 0) {
      return undefined;
    }
    const index = this.clampIndex(target);
    target.lastFocusedItem = index;
    this.activeRegion = target.name;
    return {region: target.name, index};
  }

  private clampIndex(region: ShellFocusRegionSpec): number {
    if (region.itemCount <= 0) {
      return 0;
    }
    return Math.max(0, Math.min(region.lastFocusedItem ?? 0, region.itemCount - 1));
  }
}

export const HOME_FOCUS_REGIONS = {
  spaces: 'space-switcher',
  system: 'home-system',
  strand: 'experience-switcher-game',
  trap: 'focus-trap',
} as const;

/** HOME m217/m540/m813: up from the strand reaches spaces, then right reaches system. */
export function createHomeFocusGraph(gameCount: number, spaceCount = 2): ShellFocusGraph {
  const graph = new ShellFocusGraph();
  graph.add({
    name: HOME_FOCUS_REGIONS.spaces,
    itemCount: Math.max(1, spaceCount),
    canMoveRight: true,
    rightCandidate: HOME_FOCUS_REGIONS.system,
    canMoveDown: true,
    downCandidate: HOME_FOCUS_REGIONS.strand,
  });
  graph.add({
    name: HOME_FOCUS_REGIONS.system,
    itemCount: 3,
    canMoveLeft: true,
    leftCandidate: HOME_FOCUS_REGIONS.spaces,
    canMoveDown: true,
    downCandidate: HOME_FOCUS_REGIONS.strand,
  });
  graph.add({
    name: HOME_FOCUS_REGIONS.strand,
    itemCount: Math.max(0, gameCount),
    canMoveUp: true,
    upCandidate: HOME_FOCUS_REGIONS.spaces,
  });
  graph.add({name: HOME_FOCUS_REGIONS.trap, itemCount: 1});
  graph.setActive(gameCount > 0 ? HOME_FOCUS_REGIONS.strand : HOME_FOCUS_REGIONS.trap);
  return graph;
}
