import { OperationsCenterSnapshotDto } from './operations.models';
import {
  deriveOperationsState,
  initialOperationsPollingState,
  isSnapshotStale,
  nextPollingDelayMilliseconds,
  pollingFailed,
  pollingSucceeded,
} from './operations-state';

describe('operations state', () => {
  const now = Date.parse('2026-09-30T12:00:00.000Z');
  const snapshot: OperationsCenterSnapshotDto = {
    generatedAt: new Date(now).toISOString(),
    freshnessSeconds: 30,
    nodes: [
      {
        id: 'gateway',
        label: 'Gateway',
        kind: 'service',
        state: 'Connected',
        latencyMilliseconds: 3,
        lastCheckedAt: new Date(now).toISOString(),
        lastSuccessfulAt: new Date(now).toISOString(),
        checks: [],
      },
    ],
    queues: [
      {
        name: 'runs',
        state: 'Connected',
        messagesReady: 0,
        messagesUnacknowledged: 0,
        consumers: 1,
        lastCheckedAt: new Date(now).toISOString(),
        lastSuccessfulAt: new Date(now).toISOString(),
      },
    ],
    workload: {
      totalAgents: 2,
      activeAgents: 1,
      queuedRuns: 0,
      runningRuns: 0,
      succeededRuns: 4,
      failedRuns: 1,
      recentRuns: [],
    },
  };

  it('moves through connected, degraded, disconnected and recovers', () => {
    const connected = pollingSucceeded(now);
    const degraded = pollingFailed(connected);
    const disconnected = pollingFailed(degraded);

    expect(deriveOperationsState(snapshot, connected, true, now)).toBe('Connected');
    expect(deriveOperationsState(snapshot, degraded, true, now)).toBe('Degraded');
    expect(deriveOperationsState(snapshot, disconnected, true, now)).toBe('Disconnected');
    expect(deriveOperationsState(snapshot, pollingSucceeded(now + 1), true, now + 1)).toBe(
      'Connected',
    );
  });

  it('reports unknown for missing or stale snapshots and degraded without SignalR', () => {
    expect(deriveOperationsState(null, initialOperationsPollingState, false, now)).toBe('Unknown');
    expect(isSnapshotStale(snapshot, now + 31_000)).toBe(true);
    expect(deriveOperationsState(snapshot, pollingSucceeded(now), true, now + 31_000)).toBe(
      'Unknown',
    );
    expect(deriveOperationsState(snapshot, pollingSucceeded(now), false, now)).toBe('Degraded');
  });

  it('caps polling backoff at 30 seconds', () => {
    expect([0, 1, 2, 3, 4, 20].map(nextPollingDelayMilliseconds)).toEqual([
      5_000, 5_000, 10_000, 20_000, 30_000, 30_000,
    ]);
  });
});
