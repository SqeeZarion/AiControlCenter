import { OperationsCenterSnapshotDto, OperationsConnectionState } from './operations.models';

export interface OperationsPollingState {
  consecutiveFailures: number;
  lastSuccessfulAt: number | null;
}

export const initialOperationsPollingState: OperationsPollingState = {
  consecutiveFailures: 0,
  lastSuccessfulAt: null,
};

export function pollingSucceeded(now: number): OperationsPollingState {
  return { consecutiveFailures: 0, lastSuccessfulAt: now };
}

export function pollingFailed(state: OperationsPollingState): OperationsPollingState {
  return { ...state, consecutiveFailures: state.consecutiveFailures + 1 };
}

export function nextPollingDelayMilliseconds(consecutiveFailures: number): number {
  if (consecutiveFailures <= 0) return 5_000;
  return Math.min(30_000, 5_000 * 2 ** Math.min(consecutiveFailures - 1, 3));
}

export function isSnapshotStale(
  snapshot: OperationsCenterSnapshotDto | null,
  now: number,
): boolean {
  if (!snapshot) return true;
  const generatedAt = Date.parse(snapshot.generatedAt);
  return !Number.isFinite(generatedAt) || now - generatedAt > snapshot.freshnessSeconds * 1_000;
}

export function deriveOperationsState(
  snapshot: OperationsCenterSnapshotDto | null,
  polling: OperationsPollingState,
  realtimeConnected: boolean,
  now: number,
): OperationsConnectionState {
  if (polling.consecutiveFailures >= 2) return 'Disconnected';
  if (!snapshot || isSnapshotStale(snapshot, now)) return 'Unknown';
  if (polling.consecutiveFailures === 1 || !realtimeConnected) return 'Degraded';
  const states = [
    ...snapshot.nodes.map((node) => node.state),
    ...snapshot.queues.map((q) => q.state),
  ];
  if (states.some((state) => state === 'Disconnected' || state === 'Degraded')) return 'Degraded';
  if (states.some((state) => state === 'Unknown')) return 'Unknown';
  return 'Connected';
}
