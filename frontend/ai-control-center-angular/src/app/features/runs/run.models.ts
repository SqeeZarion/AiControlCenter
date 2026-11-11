export type RunStatus =
  | 'Queued'
  | 'Running'
  | 'Succeeded'
  | 'Failed'
  | 'PausedProviderUnavailable'
  | 'PausedInsufficientFunds'
  | 'Cancelled';
export type RunStepStatus = 'Running' | 'Succeeded' | 'Failed' | 'Paused';
export type TestRunOutcome = 'Succeed' | 'Fail';

export interface AgentRunPreflightDto {
  available: boolean;
  code: string;
}

export interface RunStepDto {
  id: string;
  sequence: number;
  name: string;
  status: RunStepStatus;
  log: string | null;
  startedAt: string;
  updatedAt: string;
  completedAt: string | null;
}

export interface AgentRunDto {
  id: string;
  agentId: string;
  ownerUserId: string;
  correlationId: string;
  agentName: string;
  agentCode: string;
  agentVersion: number;
  directionId: string;
  directionName: string;
  directionCode: string;
  directionVersion: number;
  executionType: string;
  input: string;
  expectedOutcome: TestRunOutcome;
  status: RunStatus;
  revision: number;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  result: string | null;
  error: string | null;
  steps: RunStepDto[];
  attempts?: AiProviderAttemptDto[];
  modelRoutesJson?: string | null;
  maxAttempts?: number;
  maxEstimatedCostUsd?: number | null;
}

export interface AiProviderAttemptDto {
  id: string;
  sequence: number;
  provider: string;
  model: string;
  status: string;
  failureKind: string | null;
  providerRequestId: string | null;
  inputTokens: number;
  outputTokens: number;
  estimatedCostUsd: number | null;
  actualModel: string | null;
  idempotencyKey: string;
  routePriority: number;
  latencyMilliseconds: number;
  cachedInputTokens: number | null;
  totalTokens: number;
  usageSource: string;
  costSource: string;
  finishStatus: string;
  fallbackReason: string | null;
  partialOutput: string | null;
  startedAt: string;
  completedAt: string;
  errorCode: string | null;
  operation: string | null;
  generationDispatched: string | null;
  safeExceptionType: string | null;
}

export interface AgentRunListDto {
  items: AgentRunDto[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

export interface RunFilters {
  status?: RunStatus;
  agentId?: string;
  page: number;
  pageSize: number;
}
