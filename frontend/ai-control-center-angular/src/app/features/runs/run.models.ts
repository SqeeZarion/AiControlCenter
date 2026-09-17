export type RunStatus = 'Queued' | 'Running' | 'Succeeded' | 'Failed';
export type RunStepStatus = 'Running' | 'Succeeded' | 'Failed';
export type TestRunOutcome = 'Succeed' | 'Fail';

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
