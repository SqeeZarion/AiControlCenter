import { RunStatusChangedEvent } from '../../core/realtime/realtime.service';
import { formatUkrainianDate, runStatusLabel, shouldRefreshRun } from './run-presenter';

describe('run presenter', () => {
  const event: RunStatusChangedEvent = {
    eventId: 'event',
    runId: 'run',
    ownerUserId: 'user',
    status: 'Running',
    revision: 5,
    occurredAt: '2026-09-12T12:00:00Z',
    stepSequence: 1,
    stepStatus: 'Running',
  };
  it('ignores stale, duplicate and unrelated SignalR events', () => {
    expect(shouldRefreshRun('run', 4, event)).toBe(true);
    expect(shouldRefreshRun('run', 5, event)).toBe(false);
    expect(shouldRefreshRun('run', 6, event)).toBe(false);
    expect(shouldRefreshRun('other', 1, event)).toBe(false);
  });
  it('uses Ukrainian labels and a readable Ukrainian date', () => {
    expect(runStatusLabel('Queued')).toBe('У черзі');
    expect(formatUkrainianDate('2026-09-12T12:00:00Z')).toContain('2026');
  });
});
