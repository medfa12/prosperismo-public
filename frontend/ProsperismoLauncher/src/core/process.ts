import type {HostProcessEvent, ProsperismoHostGateway} from './host';
import type {GameInstall, ProcessSession} from './models';
import {DEFAULT_PROCESS_SESSION} from './models';

export function beginSession(game: GameInstall, tracked: boolean): ProcessSession {
  return {
    gamePath: game.gamePath,
    titleName: game.titleName,
    phase: 'launching',
    startedAt: new Date().toISOString(),
    tracking: tracked ? 'host-events' : 'launch-only',
  };
}

export function launchedSession(session: ProcessSession): ProcessSession {
  return {...session, phase: 'running'};
}

export function applyProcessEvent(session: ProcessSession, event: HostProcessEvent): ProcessSession {
  return {
    ...session,
    phase: event.phase,
    exitCode: event.exitCode,
    message: event.message,
    endedAt: event.phase === 'exited' || event.phase === 'failed' ? new Date().toISOString() : undefined,
  };
}

export function failedSession(session: ProcessSession, reason: unknown): ProcessSession {
  return {
    ...session,
    phase: 'failed',
    endedAt: new Date().toISOString(),
    message: reason instanceof Error ? reason.message : String(reason),
  };
}

export function subscribeToProcessLifecycle(
  host: ProsperismoHostGateway,
  update: (event: HostProcessEvent) => void,
): () => void {
  return host.subscribeProcessEvents?.(update) ?? (() => undefined);
}

export function idleSession(): ProcessSession {
  return {...DEFAULT_PROCESS_SESSION};
}
