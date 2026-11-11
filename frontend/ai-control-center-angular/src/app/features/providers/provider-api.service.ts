import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import {
  AiProviderKind,
  GitHubOAuthStartDto,
  GitHubOAuthStatusDto,
  LocalCopilotCliStatusDto,
  LocalCopilotReconnectDto,
  ProviderConnectionCapabilitiesDto,
  ProviderConnectionDto,
  ProviderModelDto,
  ProviderUsageSummaryDto,
} from './provider.models';

@Injectable({ providedIn: 'root' })
export class ProviderApiService {
  private readonly http = inject(HttpClient);
  private readonly url = '/api/integrations/v1/provider-connections';
  list() {
    return this.http.get<ProviderConnectionDto[]>(this.url);
  }
  capabilities() {
    return this.http.get<ProviderConnectionCapabilitiesDto>(`${this.url}/capabilities`);
  }
  create(provider: AiProviderKind, displayName: string, apiKey: string | null) {
    return this.http.post<ProviderConnectionDto>(this.url, { provider, displayName, apiKey });
  }
  validate(id: string) {
    return this.http.post<ProviderConnectionDto>(
      `${this.url}/${encodeURIComponent(id)}/validate`,
      {},
    );
  }
  replaceCredential(id: string, apiKey: string) {
    return this.http.put<ProviderConnectionDto>(
      `${this.url}/${encodeURIComponent(id)}/credential`,
      { apiKey },
    );
  }
  models(id: string) {
    return this.http.get<ProviderModelDto[]>(`${this.url}/${encodeURIComponent(id)}/models`);
  }
  usage(days = 30) {
    return this.http.get<ProviderUsageSummaryDto>(`${this.url}/usage`, { params: { days } });
  }
  revoke(id: string) {
    return this.http.delete<void>(`${this.url}/${encodeURIComponent(id)}`);
  }
  githubOAuthStatus() {
    return this.http.get<GitHubOAuthStatusDto>(`${this.url}/github/oauth/status`);
  }
  githubOAuthStart(redirectUri?: string, returnUrl?: string) {
    return this.http.post<GitHubOAuthStartDto>(`${this.url}/github/oauth/start`, {
      ...(redirectUri ? { redirectUri } : {}),
      ...(returnUrl ? { returnUrl } : {}),
    });
  }
  connectGitHubPat(personalAccessToken: string, expectedGitHubLogin: string) {
    return this.http.put<ProviderConnectionDto>(`${this.url}/github/pat`, {
      personalAccessToken,
      expectedGitHubLogin,
    });
  }
  localCopilotCliStatus() {
    return this.http.get<LocalCopilotCliStatusDto>(`${this.url}/local-copilot/status`);
  }
  connectLocalCopilotCli() {
    return this.http.put<ProviderConnectionDto>(`${this.url}/local-copilot`, {});
  }
  startLocalCopilotReconnect() {
    return this.http.post<LocalCopilotReconnectDto>(`${this.url}/local-copilot/reconnect`, {});
  }
  startLocalCopilotReauthentication() {
    return this.http.post<LocalCopilotReconnectDto>(`${this.url}/local-copilot/reauthenticate`, {
      confirmed: true,
    });
  }
  logoutLocalCopilotSession() {
    return this.http.post<{ status: string; errorCode: string | null }>(
      `${this.url}/local-copilot/logout`,
      { confirmed: true },
    );
  }
  localCopilotReconnect(operationId: string) {
    return this.http.get<LocalCopilotReconnectDto>(
      `${this.url}/local-copilot/reconnect/${encodeURIComponent(operationId)}`,
    );
  }
  currentLocalCopilotReconnect() {
    return this.http.get<LocalCopilotReconnectDto | null>(
      `${this.url}/local-copilot/reconnect/current`,
    );
  }
  cancelLocalCopilotReconnect(operationId: string) {
    return this.http.post<LocalCopilotReconnectDto>(
      `${this.url}/local-copilot/reconnect/${encodeURIComponent(operationId)}/cancel`,
      {},
    );
  }
}
