export type AgentStatus = 'Active' | 'Inactive';
export type AgentExecutionType = 'Test' | 'Ai';

export interface AgentDefinitionDto {
  id: string;
  directionId: string;
  name: string;
  code: string;
  description: string | null;
  status: AgentStatus;
  executionType: AgentExecutionType;
  createdAt: string;
  updatedAt: string;
  archivedAt: string | null;
  version: number;
  isArchived: boolean;
  systemPrompt?: string | null;
  modelRoutesJson?: string | null;
  toolPolicyJson?: string | null;
  maxInputTokens?: number | null;
  maxOutputTokens?: number | null;
  maxEstimatedCostUsd?: number | null;
  maxAttempts?: number;
}

export interface AgentDefinitionListDto {
  items: AgentDefinitionDto[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

export interface AgentFilters {
  includeArchived: boolean;
  status?: AgentStatus;
  directionId?: string;
  search?: string;
  page: number;
  pageSize: number;
}

export interface AgentContent {
  directionId: string;
  name: string;
  code: string;
  description: string | null;
  status: AgentStatus;
  executionType: AgentExecutionType;
  systemPrompt?: string | null;
  modelRoutesJson?: string | null;
  toolPolicyJson?: string | null;
  maxInputTokens?: number | null;
  maxOutputTokens?: number | null;
  maxEstimatedCostUsd?: number | null;
  maxAttempts?: number;
}

export type CreateAgentRequest = AgentContent;
export interface UpdateAgentRequest extends AgentContent {
  version: number;
}
