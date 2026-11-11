import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { DirectionApiService } from '../directions/direction-api.service';
import { ProviderApiService } from '../providers/provider-api.service';
import { ProviderConnectionDto } from '../providers/provider.models';
import { AgentApiService } from './agent-api.service';
import { AgentFormComponent } from './agent-form.component';

describe('AgentFormComponent AI routing', () => {
  let providerApi: Record<string, ReturnType<typeof vi.fn>>;
  let agentApi: Record<string, ReturnType<typeof vi.fn>>;

  beforeEach(async () => {
    providerApi = {
      list: vi.fn(() =>
        of([connection('github', 'GitHubCopilot'), connection('openai', 'OpenAi')]),
      ),
      models: vi.fn(() =>
        of([
          {
            id: 'model-1',
            displayName: 'Model 1',
            contextWindow: null,
            supportsTools: true,
            supportsStreaming: false,
            inputUsdPerMillionTokens: null,
            outputUsdPerMillionTokens: null,
          },
        ]),
      ),
      capabilities: vi.fn(() =>
        of({
          githubCopilotOAuthEnabled: true,
          githubCopilotPatEnabled: true,
          localCopilotCliEnabled: true,
          localCopilotCliReauthenticationEnabled: false,
          localCopilotCliLogoutEnabled: false,
        }),
      ),
      localCopilotCliStatus: vi.fn(() =>
        of({
          enabled: true,
          availableForCurrentUser: true,
          runtimeState: 'Ready',
          sidecarState: 'Ready',
          authenticationState: 'Active',
        }),
      ),
    };
    agentApi = {
      create: vi.fn(() => of({})),
      update: vi.fn(() => of({})),
      get: vi.fn(),
    };
    await TestBed.configureTestingModule({
      imports: [AgentFormComponent],
      providers: [
        provideRouter([]),
        { provide: AgentApiService, useValue: agentApi },
        {
          provide: DirectionApiService,
          useValue: {
            list: vi.fn(() =>
              of({ items: [], page: 1, pageSize: 100, totalCount: 0, totalPages: 0 }),
            ),
          },
        },
        { provide: ProviderApiService, useValue: providerApi },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({}) } } },
      ],
    }).compileComponents();
  });

  it('builds an ordered paid fallback preset with explicit caps and permissions', async () => {
    const fixture = TestBed.createComponent(AgentFormComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.componentInstance.providerConnections.set([
      connection('github', 'GitHubCopilot'),
      connection('openai', 'OpenAi'),
    ]);
    fixture.componentInstance.form.controls.maxEstimatedCostUsd.setValue(2);

    fixture.componentInstance.applyPreset('GitHubThenOpenAI');
    const routes = fixture.componentInstance.routes();

    expect(routes.map((route) => route.priority)).toEqual([1, 2]);
    expect(routes[0].paidExecutionAllowed).toBe(false);
    expect(routes[1]).toMatchObject({
      provider: 'OpenAi',
      paidExecutionAllowed: true,
      paidFallbackAllowed: true,
      safetyCapUsd: 2,
      dailyCostCapUsd: 20,
      monthlyCostCapUsd: 200,
    });
  });

  it('reorders and removes routes while keeping contiguous priorities', async () => {
    const fixture = TestBed.createComponent(AgentFormComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.componentInstance.providerConnections.set([
      connection('github', 'GitHubCopilot'),
      connection('openai', 'OpenAi'),
    ]);
    fixture.componentInstance.applyPreset('GitHubThenOpenAI');

    fixture.componentInstance.moveRoute(1, -1);
    expect(
      fixture.componentInstance.routes().map((route) => [route.provider, route.priority]),
    ).toEqual([
      ['OpenAi', 1],
      ['GitHubCopilot', 2],
    ]);

    fixture.componentInstance.removeRoute(0);
    expect(fixture.componentInstance.routes().map((route) => route.priority)).toEqual([1]);
  });

  it('loads only the selected connection model catalog', async () => {
    const fixture = TestBed.createComponent(AgentFormComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.componentInstance.providerConnections.set([
      connection('github', 'GitHubCopilot'),
      connection('openai', 'OpenAi'),
    ]);
    fixture.componentInstance.routeForm.controls.connectionId.setValue('github');

    await fixture.componentInstance.connectionChanged();

    expect(providerApi['models']).toHaveBeenCalledWith('github');
    expect(fixture.componentInstance.routeModels().map((model) => model.id)).toEqual(['model-1']);
  });

  it('offers an eligible Local Copilot CLI preset and auto-selects the only connection', async () => {
    const fixture = TestBed.createComponent(AgentFormComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    const local = connection('local-cli', 'GitHubCopilotLocalCli');
    local.externalAccountLogin = 'romanpetrukhapp2025-hub';
    local.lastValidatedAt = '2026-11-02T10:00:00Z';
    local.lastSyncedAt = '2026-11-02T10:01:00Z';
    fixture.componentInstance.providerConnections.set([local]);
    fixture.componentInstance.providerCapabilities.set({
      githubCopilotOAuthEnabled: true,
      githubCopilotPatEnabled: true,
      localCopilotCliEnabled: true,
      localCopilotCliReauthenticationEnabled: false,
      localCopilotCliLogoutEnabled: false,
    });
    fixture.componentInstance.localCliStatus.set({
      enabled: true,
      availableForCurrentUser: true,
      runtimeState: 'Ready',
      sidecarState: 'Ready',
      authenticationState: 'Active',
    });

    fixture.componentInstance.applyPreset('LocalCopilotCli' as never);

    const routes = fixture.componentInstance.routes() as unknown as Array<Record<string, unknown>>;
    expect(routes).toHaveLength(1);
    expect(routes[0]).toMatchObject({
      connectionId: 'local-cli',
      provider: 'LocalCopilotCli',
      modelId: 'Auto',
      priority: 1,
      maxAttempts: 1,
      timeoutSeconds: 60,
      paidExecutionAllowed: false,
      paidFallbackAllowed: false,
    });
    expect(fixture.componentInstance.selectedConnection()?.id).toBe('local-cli');
  });

  it('keeps existing routes unchanged when the Local Copilot CLI dependency is unavailable', async () => {
    const fixture = TestBed.createComponent(AgentFormComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.componentInstance.providerConnections.set([connection('github', 'GitHubCopilot')]);
    const original = fixture.componentInstance.form.controls.modelRoutesJson.value;

    expect(fixture.componentInstance.presetAvailable('LocalCopilotCli')).toBe(false);
    fixture.componentInstance.applyPreset('LocalCopilotCli');

    expect(fixture.componentInstance.form.controls.modelRoutesJson.value).toBe(original);
    expect(fixture.componentInstance.routeError()).toContain('Підключення не знайдено');
  });

  it('saves the single Local CLI route with an explicit owner-scoped connection binding', async () => {
    const fixture = TestBed.createComponent(AgentFormComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    const local = connection('local-cli', 'GitHubCopilotLocalCli');
    local.lastSyncedAt = '2026-11-02T10:01:00Z';
    fixture.componentInstance.providerConnections.set([local]);
    makeLocalEligible(fixture.componentInstance);
    fixture.componentInstance.form.patchValue({
      directionId: '00000000-0000-0000-0000-000000000001',
      name: 'Local AI agent',
      code: 'local-ai-agent',
      executionType: 'Ai',
      systemPrompt: 'bounded system prompt',
    });

    fixture.componentInstance.applyPreset('LocalCopilotCli');
    await fixture.componentInstance.submit();

    expect(agentApi['create']).toHaveBeenCalledOnce();
    const payload = agentApi['create'].mock.calls[0][0] as { modelRoutesJson: string };
    expect(JSON.parse(payload.modelRoutesJson)).toEqual([
      expect.objectContaining({
        provider: 'LocalCopilotCli',
        connectionId: 'local-cli',
        modelId: 'Auto',
        paidExecutionAllowed: false,
        paidFallbackAllowed: false,
      }),
    ]);
  });

  it('shows typed route validation inline with its safe correlation id', async () => {
    agentApi['create'].mockReturnValue(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 400,
            error: {
              code: 'AgentRouteConnectionUnavailable',
              field: 'modelRoutesJson',
              correlationId: 'safe-correlation-id',
            },
          }),
      ),
    );
    const fixture = TestBed.createComponent(AgentFormComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    const local = connection('local-cli', 'GitHubCopilotLocalCli');
    local.lastSyncedAt = '2026-11-02T10:01:00Z';
    fixture.componentInstance.providerConnections.set([local]);
    makeLocalEligible(fixture.componentInstance);
    fixture.componentInstance.form.patchValue({
      directionId: '00000000-0000-0000-0000-000000000001',
      name: 'Local AI agent',
      code: 'local-ai-agent',
      executionType: 'Ai',
      systemPrompt: 'bounded system prompt',
    });
    fixture.componentInstance.applyPreset('LocalCopilotCli');

    await fixture.componentInstance.submit();

    expect(fixture.componentInstance.routeError()).toContain('Вибране підключення недоступне');
    expect(fixture.componentInstance.routeError()).toContain('safe-correlation-id');
  });
});

function makeLocalEligible(component: AgentFormComponent): void {
  component.providerCapabilities.set({
    githubCopilotOAuthEnabled: true,
    githubCopilotPatEnabled: true,
    localCopilotCliEnabled: true,
    localCopilotCliReauthenticationEnabled: false,
    localCopilotCliLogoutEnabled: false,
  });
  component.localCliStatus.set({
    enabled: true,
    availableForCurrentUser: true,
    runtimeState: 'Ready',
    sidecarState: 'Ready',
    authenticationState: 'Active',
  });
}

function connection(
  id: string,
  provider: ProviderConnectionDto['provider'],
): ProviderConnectionDto {
  return {
    id,
    provider,
    displayName: provider,
    status: 'Active',
    credentialFingerprint: null,
    lastValidatedAt: null,
    lastSyncedAt: null,
    revokedAt: null,
    credentialVersion: 1,
    quotaSource: 'Unavailable',
    quotaState: 'Unavailable',
    lastErrorCategory: null,
    lastSafeError: null,
    externalAccountId: null,
    externalAccountLogin: null,
    version: 1,
  };
}
