export type AiProviderKind =
  'GitHubCopilot' | 'GitHubCopilotLocalCli' | 'OpenAi' | 'XAi' | 'DeterministicTest';
export interface LocalCopilotCliStatusDto {
  enabled: boolean;
  availableForCurrentUser: boolean;
  runtimeState: string;
  sidecarState?: 'Ready' | 'Unavailable';
  authenticationState?: 'Active' | 'Unauthenticated' | 'Unknown';
}
export interface ProviderConnectionCapabilitiesDto {
  githubCopilotOAuthEnabled: boolean;
  githubCopilotPatEnabled: boolean;
  localCopilotCliEnabled: boolean;
  localCopilotCliReauthenticationEnabled?: boolean;
  localCopilotCliLogoutEnabled?: boolean;
}
export type LocalCopilotReconnectState =
  | 'Starting'
  | 'StartingLogin'
  | 'WaitingForUser'
  | 'GitHubAuthorized'
  | 'PersistingCredential'
  | 'RestartingRuntime'
  | 'Validating'
  | 'SynchronizingModels'
  | 'Cancelling'
  | 'Authorized'
  | 'LoginFailed'
  | 'AuthorizationFailed'
  | 'RuntimeRestartFailed'
  | 'ValidationFailed'
  | 'ModelSyncFailed'
  | 'Expired'
  | 'Cancelled';
export interface LocalCopilotReconnectDto {
  operationId: string;
  state: LocalCopilotReconnectState;
  verificationUri: string | null;
  userCode: string | null;
  expiresAt: string;
  errorCode: string | null;
  ownerId?: string;
  correlationId?: string | null;
  processExitCode?: number | null;
  failureReason?: string | null;
  canCancel?: boolean;
}
export type GitHubOAuthConfigurationStatus = 'Available' | 'NotConfigured';
export interface GitHubOAuthStatusDto {
  status: GitHubOAuthConfigurationStatus;
}
export interface GitHubOAuthStartDto {
  status: GitHubOAuthConfigurationStatus;
  authorizationUri: string | null;
}
export interface ProviderConnectionDto {
  id: string;
  provider: AiProviderKind;
  displayName: string;
  status:
    | 'Active'
    | 'Degraded'
    | 'RequiresReconnect'
    | 'NotEntitled'
    | 'PolicyBlocked'
    | 'OAuthAppUnsupported'
    | 'InsufficientPermissions'
    | 'QuotaExhausted'
    | 'Invalid'
    | 'Revoked';
  credentialFingerprint: string | null;
  lastValidatedAt: string | null;
  lastSyncedAt: string | null;
  revokedAt: string | null;
  credentialVersion: number;
  quotaSource: 'ProviderReported' | 'Estimated' | 'Unavailable';
  quotaState: string;
  lastErrorCategory: string | null;
  lastSafeError: string | null;
  externalAccountId: string | null;
  externalAccountLogin: string | null;
  version: number;
  credentialExpiresAt?: string | null;
  expirationSource?: string;
  lastSuccessfulValidationAt?: string | null;
  lastModelSyncAt?: string | null;
  nextValidationAt?: string | null;
  authenticationStatus?: string;
  modelCatalogStatus?: string;
  credentialStatusSource?: string;
}
export interface ProviderModelDto {
  id: string;
  displayName: string;
  contextWindow: number | null;
  supportsTools: boolean;
  supportsStreaming: boolean;
  inputUsdPerMillionTokens: number | null;
  outputUsdPerMillionTokens: number | null;
}
export interface ProviderUsageSummaryDto {
  requestCount: number;
  inputTokens: number;
  cachedInputTokens: number | null;
  outputTokens: number;
  estimatedCostUsd: number;
  since: string;
  usageSource: string;
  costSource: string;
}
