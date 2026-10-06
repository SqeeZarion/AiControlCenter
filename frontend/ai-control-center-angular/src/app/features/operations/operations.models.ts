export type OperationsConnectionState = 'Connected' | 'Degraded' | 'Disconnected' | 'Unknown';

export interface OperationsHealthCheckDto {
  name: string;
  status: string;
  durationMilliseconds: number;
}

export interface OperationsNodeDto {
  id: string;
  label: string;
  kind: string;
  state: OperationsConnectionState;
  latencyMilliseconds: number | null;
  lastCheckedAt: string;
  lastSuccessfulAt: string | null;
  checks: OperationsHealthCheckDto[];
}

export interface OperationsQueueDto {
  name: string;
  state: OperationsConnectionState;
  messagesReady: number | null;
  messagesUnacknowledged: number | null;
  consumers: number | null;
  lastCheckedAt: string;
  lastSuccessfulAt: string | null;
}

export interface OperationsRunDto {
  id: string;
  agentName: string;
  directionName: string;
  status: string;
  revision: number;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  activeStepSequence: number | null;
  activeStepName: string | null;
  activeStepStatus: string | null;
}

export interface OperationsWorkloadDto {
  totalAgents: number | null;
  activeAgents: number | null;
  queuedRuns: number | null;
  runningRuns: number | null;
  succeededRuns: number | null;
  failedRuns: number | null;
  recentRuns: OperationsRunDto[];
}

export interface OperationsCenterSnapshotDto {
  generatedAt: string;
  freshnessSeconds: number;
  nodes: OperationsNodeDto[];
  queues: OperationsQueueDto[];
  workload: OperationsWorkloadDto;
}
