import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, Subject, throwError } from 'rxjs';
import { ProviderApiService } from './provider-api.service';
import {
  PROVIDER_CLIPBOARD,
  PROVIDER_OAUTH_NAVIGATOR,
  ProviderConnectionsComponent,
} from './provider-connections.component';
import { AiProviderKind, ProviderConnectionDto } from './provider.models';

describe('ProviderConnectionsComponent', () => {
  let api: Record<string, ReturnType<typeof vi.fn>>;
  let navigateToOAuth: ReturnType<typeof vi.fn>;
  let copyToClipboard: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    navigateToOAuth = vi.fn();
    copyToClipboard = vi.fn(() => Promise.resolve());
    api = {
      capabilities: vi.fn(() =>
        of({
          githubCopilotOAuthEnabled: true,
          githubCopilotPatEnabled: true,
          localCopilotCliEnabled: false,
          localCopilotCliReauthenticationEnabled: false,
          localCopilotCliLogoutEnabled: false,
        }),
      ),
      list: vi.fn(() => of([connection()])),
      usage: vi.fn(() =>
        of({
          requestCount: 1,
          inputTokens: 2,
          cachedInputTokens: null,
          outputTokens: 3,
          estimatedCostUsd: 0.01,
          since: '2026-10-01T00:00:00Z',
          usageSource: 'ProviderReported',
          costSource: 'Estimated',
        }),
      ),
      create: vi.fn(() => of(connection())),
      replaceCredential: vi.fn(() => of(connection({ credentialVersion: 2 }))),
      validate: vi.fn(() => of(connection())),
      models: vi.fn(() => of([])),
      revoke: vi.fn(() => of(undefined)),
      githubOAuthStatus: vi.fn(() => of({ status: 'Available' })),
      githubOAuthStart: vi.fn(() =>
        of({
          status: 'Available',
          authorizationUri: 'https://github.com/login/oauth/authorize?client_id=client-id',
        }),
      ),
      connectGitHubPat: vi.fn(() =>
        of(connection({ provider: 'GitHubCopilot', externalAccountLogin: 'expected-user' })),
      ),
      localCopilotCliStatus: vi.fn(() =>
        of({ enabled: false, availableForCurrentUser: false, runtimeState: 'Disabled' }),
      ),
      connectLocalCopilotCli: vi.fn(() =>
        of(connection({ provider: 'GitHubCopilotLocalCli', credentialFingerprint: null })),
      ),
      startLocalCopilotReconnect: vi.fn(() =>
        of({
          operationId: 'operation-1',
          state: 'WaitingForUser',
          verificationUri: 'https://github.com/login/device',
          userCode: 'ABCD-EFGH',
          expiresAt: '2026-10-30T12:15:00Z',
          errorCode: null,
        }),
      ),
      startLocalCopilotReauthentication: vi.fn(() =>
        of({
          operationId: 'operation-force-1',
          state: 'WaitingForUser',
          verificationUri: 'https://github.com/login/device',
          userCode: 'WXYZ-1234',
          expiresAt: '2026-10-30T12:15:00Z',
          errorCode: null,
        }),
      ),
      currentLocalCopilotReconnect: vi.fn(() => of(null)),
      logoutLocalCopilotSession: vi.fn(() => of({ status: 'Succeeded', errorCode: null })),
      localCopilotReconnect: vi.fn(() =>
        of({
          operationId: 'operation-1',
          state: 'Authorized',
          verificationUri: null,
          userCode: null,
          expiresAt: '2026-10-30T12:15:00Z',
          errorCode: null,
        }),
      ),
      cancelLocalCopilotReconnect: vi.fn(() =>
        of({
          operationId: 'operation-1',
          state: 'Cancelled',
          verificationUri: null,
          userCode: null,
          expiresAt: '2026-10-30T12:15:00Z',
          errorCode: null,
        }),
      ),
    };
    await TestBed.configureTestingModule({
      imports: [ProviderConnectionsComponent],
      providers: [
        { provide: ProviderApiService, useValue: api },
        { provide: PROVIDER_OAUTH_NAVIGATOR, useValue: navigateToOAuth },
        { provide: PROVIDER_CLIPBOARD, useValue: copyToClipboard },
      ],
    }).compileComponents();
  });

  it('hides the credential input for Deterministic Test and explains the local provider', async () => {
    const fixture = await createFixture();

    expect(fixture.nativeElement.querySelector('[data-testid="create-credential"]')).toBeNull();
    expect(
      fixture.nativeElement.querySelector('[data-testid="deterministic-help"]')?.textContent,
    ).toContain('не потребує API-ключа');
  });

  it.each<AiProviderKind>(['OpenAi', 'XAi'])(
    'shows a write-only credential input for %s',
    async (provider) => {
      const fixture = await createFixture();
      fixture.componentInstance.form.controls.provider.setValue(provider);
      fixture.componentInstance.providerChanged();
      fixture.detectChanges();

      const input = fixture.nativeElement.querySelector(
        '[data-testid="create-credential"]',
      ) as HTMLInputElement;
      expect(input.type).toBe('password');
      expect(input.autocomplete).toBe('new-password');
    },
  );

  it('exposes GitHub Copilot through the OAuth action', async () => {
    const fixture = await createFixture();

    (
      fixture.nativeElement.querySelector('[data-testid="github-oauth"]') as HTMLButtonElement
    ).click();
    await fixture.whenStable();

    expect(api['githubOAuthStart']).toHaveBeenCalledOnce();
    expect(navigateToOAuth).toHaveBeenCalledOnce();
    expect(fixture.nativeElement.textContent).not.toContain('GitHub Copilot API key');
  });

  it('disables OAuth and never navigates when GitHub OAuth is not configured', async () => {
    api['githubOAuthStatus'].mockReturnValue(of({ status: 'NotConfigured' }));
    const fixture = await createFixture();
    const button = fixture.nativeElement.querySelector(
      '[data-testid="github-oauth"]',
    ) as HTMLButtonElement;

    expect(button.disabled).toBe(true);
    expect(
      fixture.nativeElement.querySelector('[data-testid="github-oauth-not-configured"]')
        ?.textContent,
    ).toContain('GitHub OAuth не налаштовано');

    await fixture.componentInstance.connectGitHub();

    expect(api['githubOAuthStart']).not.toHaveBeenCalled();
    expect(navigateToOAuth).not.toHaveBeenCalled();
  });

  it('does not navigate when start returns the typed NotConfigured status', async () => {
    api['githubOAuthStart'].mockReturnValue(
      of({ status: 'NotConfigured', authorizationUri: null }),
    );
    const fixture = await createFixture();

    await fixture.componentInstance.connectGitHub();
    fixture.detectChanges();

    expect(navigateToOAuth).not.toHaveBeenCalled();
    expect(fixture.componentInstance.error()).toBe('GitHub OAuth не налаштовано');
  });

  it('keeps OAuth available and exposes PAT as a separate write-only fallback', async () => {
    const fixture = await createFixture();

    (
      fixture.nativeElement.querySelector('[data-testid="github-pat-toggle"]') as HTMLButtonElement
    ).click();
    fixture.detectChanges();
    const input = fixture.nativeElement.querySelector(
      '[data-testid="github-pat-credential"]',
    ) as HTMLInputElement;

    expect(fixture.nativeElement.querySelector('[data-testid="github-oauth"]')).not.toBeNull();
    expect(input.type).toBe('password');
    expect(input.autocomplete).toBe('new-password');
    expect(fixture.nativeElement.textContent).toContain('Copilot Requests');
    expect(fixture.nativeElement.textContent).toContain('ghp_');
  });

  it('hides Local Copilot CLI unless the backend enables it for the current owner', async () => {
    const fixture = await createFixture();

    expect(fixture.nativeElement.querySelector('[data-testid="local-copilot-card"]')).toBeNull();
  });

  it('shows the official device-flow modal without exposing a credential', async () => {
    api['capabilities'].mockReturnValue(
      of({
        githubCopilotOAuthEnabled: false,
        githubCopilotPatEnabled: false,
        localCopilotCliEnabled: true,
        localCopilotCliReauthenticationEnabled: true,
        localCopilotCliLogoutEnabled: false,
      }),
    );
    api['localCopilotCliStatus'].mockReturnValue(
      of({ enabled: true, availableForCurrentUser: true, runtimeState: 'Configured' }),
    );
    const fixture = await createFixture();
    const card = fixture.nativeElement.querySelector('[data-testid="local-copilot-card"]');

    expect(card?.textContent).toContain('лише для локальної розробки');
    expect(card?.textContent).toContain('не зберігається');

    (card?.querySelector('[data-testid="local-copilot-connect"]') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(api['startLocalCopilotReconnect']).toHaveBeenCalledOnce();
    fixture.detectChanges();
    const modal = fixture.nativeElement.querySelector(
      '[data-testid="local-copilot-reconnect-modal"]',
    );
    expect(modal?.textContent).toContain('https://github.com/login/device');
    expect(modal?.textContent).toContain('ABCD-EFGH');
    expect(modal?.textContent).not.toContain('github_pat_');
    expect(modal?.textContent).not.toContain('gho_');
    expect(modal?.textContent).not.toContain('Authorization');
  });

  it('hides disabled OAuth and PAT actions using backend capabilities', async () => {
    api['capabilities'].mockReturnValue(
      of({
        githubCopilotOAuthEnabled: false,
        githubCopilotPatEnabled: false,
        localCopilotCliEnabled: true,
        localCopilotCliReauthenticationEnabled: false,
        localCopilotCliLogoutEnabled: false,
      }),
    );
    api['localCopilotCliStatus'].mockReturnValue(
      of({ enabled: true, availableForCurrentUser: true, runtimeState: 'Ready' }),
    );

    const fixture = await createFixture();

    expect(fixture.nativeElement.querySelector('[data-testid="github-oauth"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="github-pat-toggle"]')).toBeNull();
    expect(
      fixture.nativeElement.querySelector('[data-testid="local-copilot-reauthenticate"]'),
    ).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="local-copilot-logout"]')).toBeNull();
    expect(api['githubOAuthStatus']).not.toHaveBeenCalled();
  });

  it('uses validation and connect labels from the actual Local CLI runtime state', async () => {
    const fixture = await createFixture();

    fixture.componentInstance.localCliStatus.set({
      enabled: true,
      availableForCurrentUser: true,
      runtimeState: 'AuthorizationRequired',
    });
    expect(fixture.componentInstance.localCopilotActionLabel()).toBe(
      'Підключити Local Copilot CLI',
    );

    fixture.componentInstance.localCliStatus.set({
      enabled: true,
      availableForCurrentUser: true,
      runtimeState: 'Ready',
    });
    expect(fixture.componentInstance.localCopilotActionLabel()).toBe('Перевірити підключення');
  });

  it('keeps validation on the fast path when an existing credential is ready', async () => {
    api['capabilities'].mockReturnValue(
      of({
        githubCopilotOAuthEnabled: false,
        githubCopilotPatEnabled: false,
        localCopilotCliEnabled: true,
        localCopilotCliReauthenticationEnabled: true,
        localCopilotCliLogoutEnabled: false,
      }),
    );
    api['localCopilotCliStatus'].mockReturnValue(
      of({ enabled: true, availableForCurrentUser: true, runtimeState: 'Ready' }),
    );
    const fixture = await createFixture();
    const validate = fixture.nativeElement.querySelector(
      '[data-testid="local-copilot-connect"]',
    ) as HTMLButtonElement;

    expect(validate.textContent).toContain('Перевірити підключення');
    validate.click();
    await fixture.whenStable();

    expect(api['startLocalCopilotReconnect']).toHaveBeenCalledOnce();
    expect(api['startLocalCopilotReauthentication']).not.toHaveBeenCalled();
  });

  it('requires confirmation before force reauthentication and cancel changes nothing', async () => {
    api['capabilities'].mockReturnValue(
      of({
        githubCopilotOAuthEnabled: false,
        githubCopilotPatEnabled: false,
        localCopilotCliEnabled: true,
        localCopilotCliReauthenticationEnabled: true,
        localCopilotCliLogoutEnabled: false,
      }),
    );
    api['localCopilotCliStatus'].mockReturnValue(
      of({ enabled: true, availableForCurrentUser: true, runtimeState: 'Ready' }),
    );
    const fixture = await createFixture();
    const reauthenticate = fixture.nativeElement.querySelector(
      '[data-testid="local-copilot-reauthenticate"]',
    ) as HTMLButtonElement;

    reauthenticate.click();
    fixture.detectChanges();
    const confirmation = fixture.nativeElement.querySelector(
      '[data-testid="local-copilot-reauthentication-confirmation"]',
    );
    expect(confirmation.textContent).toContain('Чинна локальна сесія залишиться доступною');
    expect(confirmation.textContent).toContain('GitHub-підписка');
    (
      confirmation.querySelector(
        '[data-testid="local-copilot-reauthentication-cancel"]',
      ) as HTMLButtonElement
    ).click();
    fixture.detectChanges();

    expect(api['startLocalCopilotReauthentication']).not.toHaveBeenCalled();
    expect(
      fixture.nativeElement.querySelector(
        '[data-testid="local-copilot-reauthentication-confirmation"]',
      ),
    ).toBeNull();
  });

  it('keeps validation separate from reauthentication for an invalid credential', async () => {
    api['capabilities'].mockReturnValue(
      of({
        githubCopilotOAuthEnabled: false,
        githubCopilotPatEnabled: false,
        localCopilotCliEnabled: true,
        localCopilotCliReauthenticationEnabled: true,
        localCopilotCliLogoutEnabled: false,
      }),
    );
    api['localCopilotCliStatus'].mockReturnValue(
      of({ enabled: true, availableForCurrentUser: true, runtimeState: 'AuthorizationRequired' }),
    );
    api['list'].mockReturnValue(
      of([connection({ provider: 'GitHubCopilotLocalCli', status: 'RequiresReconnect' })]),
    );
    const fixture = await createFixture();
    const connect = fixture.nativeElement.querySelector(
      '[data-testid="local-copilot-connect"]',
    ) as HTMLButtonElement;

    expect(connect.textContent).toContain('Підключити Local Copilot CLI');
    connect.click();
    fixture.detectChanges();

    expect(api['startLocalCopilotReconnect']).toHaveBeenCalledOnce();
    expect(api['startLocalCopilotReauthentication']).not.toHaveBeenCalled();
  });

  it('starts force reauthentication once after explicit confirmation', async () => {
    api['capabilities'].mockReturnValue(
      of({
        githubCopilotOAuthEnabled: false,
        githubCopilotPatEnabled: false,
        localCopilotCliEnabled: true,
        localCopilotCliReauthenticationEnabled: true,
        localCopilotCliLogoutEnabled: false,
      }),
    );
    api['localCopilotCliStatus'].mockReturnValue(
      of({ enabled: true, availableForCurrentUser: true, runtimeState: 'Ready' }),
    );
    const fixture = await createFixture();

    fixture.componentInstance.openLocalCopilotReauthenticationConfirmation();
    await fixture.componentInstance.confirmLocalCopilotReauthentication();
    await fixture.componentInstance.confirmLocalCopilotReauthentication();
    fixture.detectChanges();

    expect(api['startLocalCopilotReauthentication']).toHaveBeenCalledOnce();
    expect(api['startLocalCopilotReconnect']).not.toHaveBeenCalled();
    const modal = fixture.nativeElement.querySelector(
      '[data-testid="local-copilot-reconnect-modal"]',
    );
    expect(modal.textContent).toContain('https://github.com/login/device');
    expect(modal.textContent).toContain('WXYZ-1234');
  });

  it('keeps Local CLI logout as a separate explicitly confirmed action', async () => {
    api['capabilities'].mockReturnValue(
      of({
        githubCopilotOAuthEnabled: false,
        githubCopilotPatEnabled: false,
        localCopilotCliEnabled: true,
        localCopilotCliReauthenticationEnabled: false,
        localCopilotCliLogoutEnabled: true,
      }),
    );
    api['localCopilotCliStatus'].mockReturnValue(
      of({ enabled: true, availableForCurrentUser: true, runtimeState: 'Ready' }),
    );
    const fixture = await createFixture();

    const logout = fixture.nativeElement.querySelector(
      '[data-testid="local-copilot-logout"]',
    ) as HTMLButtonElement;
    logout.click();
    fixture.detectChanges();

    expect(
      fixture.nativeElement.querySelector('[data-testid="local-copilot-logout-confirmation"]'),
    ).not.toBeNull();
    expect(api['logoutLocalCopilotSession']).not.toHaveBeenCalled();
    fixture.componentInstance.cancelLocalCopilotLogout();
    fixture.detectChanges();
    expect(api['logoutLocalCopilotSession']).not.toHaveBeenCalled();
  });

  it('restores an active device-flow operation after reload without starting another login', async () => {
    api['capabilities'].mockReturnValue(
      of({
        githubCopilotOAuthEnabled: false,
        githubCopilotPatEnabled: false,
        localCopilotCliEnabled: true,
      }),
    );
    api['localCopilotCliStatus'].mockReturnValue(
      of({ enabled: true, availableForCurrentUser: true, runtimeState: 'Ready' }),
    );
    api['currentLocalCopilotReconnect'].mockReturnValue(
      of({
        operationId: 'operation-resumed',
        state: 'WaitingForUser',
        verificationUri: 'https://github.com/login/device',
        userCode: 'REST-ORED',
        expiresAt: '2026-10-30T12:15:00Z',
        errorCode: null,
      }),
    );

    const fixture = await createFixture();
    fixture.detectChanges();

    expect(api['currentLocalCopilotReconnect']).toHaveBeenCalledOnce();
    expect(api['startLocalCopilotReconnect']).not.toHaveBeenCalled();
    expect(api['startLocalCopilotReauthentication']).not.toHaveBeenCalled();
    expect(fixture.componentInstance.localReconnect()?.operationId).toBe('operation-resumed');
    expect(fixture.componentInstance.showLocalReconnect()).toBe(true);
  });

  it('polls the same operation and reloads connection and models after authorization', async () => {
    api['capabilities'].mockReturnValue(
      of({
        githubCopilotOAuthEnabled: false,
        githubCopilotPatEnabled: false,
        localCopilotCliEnabled: true,
      }),
    );
    api['localCopilotCliStatus'].mockReturnValue(
      of({ enabled: true, availableForCurrentUser: true, runtimeState: 'AuthorizationRequired' }),
    );
    const fixture = await createFixture();
    vi.useFakeTimers();
    try {
      await fixture.componentInstance.connectLocalCopilotCli();

      await vi.advanceTimersByTimeAsync(1500);
      await Promise.resolve();
      fixture.detectChanges();

      expect(api['localCopilotReconnect']).toHaveBeenCalledWith('operation-1');
      expect(api['capabilities']).toHaveBeenCalledTimes(2);
      expect(api['list']).toHaveBeenCalledTimes(2);
      expect(fixture.componentInstance.showLocalReconnect()).toBe(false);
      expect(fixture.componentInstance.connectingLocalCli()).toBe(false);
      expect(fixture.componentInstance.success()).toContain('авторизовано');
    } finally {
      vi.useRealTimers();
    }
  });

  it('shows a typed Local CLI error with its safe correlation id', async () => {
    api['capabilities'].mockReturnValue(
      of({
        githubCopilotOAuthEnabled: false,
        githubCopilotPatEnabled: false,
        localCopilotCliEnabled: true,
        localCopilotCliReauthenticationEnabled: false,
        localCopilotCliLogoutEnabled: false,
      }),
    );
    api['localCopilotCliStatus'].mockReturnValue(
      of({ enabled: true, availableForCurrentUser: true, runtimeState: 'Ready' }),
    );
    api['startLocalCopilotReconnect'].mockReturnValue(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 503,
            error: { code: 'SidecarUnavailable', traceId: 'safe-correlation-1' },
          }),
      ),
    );
    const fixture = await createFixture();

    await fixture.componentInstance.connectLocalCopilotCli();

    expect(fixture.componentInstance.error()).toContain('Local Copilot sidecar недоступний');
    expect(fixture.componentInstance.error()).toContain('safe-correlation-1');
  });

  it('keeps polling the same operation after a transient 429 and completes once', async () => {
    api['capabilities'].mockReturnValue(
      of({
        githubCopilotOAuthEnabled: false,
        githubCopilotPatEnabled: false,
        localCopilotCliEnabled: true,
        localCopilotCliReauthenticationEnabled: false,
        localCopilotCliLogoutEnabled: false,
      }),
    );
    api['localCopilotCliStatus'].mockReturnValue(
      of({ enabled: true, availableForCurrentUser: true, runtimeState: 'AuthorizationRequired' }),
    );
    api['startLocalCopilotReconnect'].mockReturnValue(
      of({
        operationId: 'operation-rate-limit',
        state: 'WaitingForUser',
        verificationUri: 'https://github.com/login/device',
        userCode: 'ABCD-EFGH',
        expiresAt: '2099-10-30T12:15:00Z',
        errorCode: null,
      }),
    );
    api['localCopilotReconnect']
      .mockReturnValueOnce(
        throwError(
          () =>
            new HttpErrorResponse({
              status: 429,
              error: { code: 'RateLimited', traceId: 'safe-correlation-2' },
            }),
        ),
      )
      .mockReturnValueOnce(
        of({
          operationId: 'operation-rate-limit',
          state: 'Authorized',
          verificationUri: null,
          userCode: null,
          expiresAt: '2099-10-30T12:15:00Z',
          errorCode: null,
        }),
      );
    const fixture = await createFixture();
    vi.useFakeTimers();
    try {
      await fixture.componentInstance.connectLocalCopilotCli();
      await vi.advanceTimersByTimeAsync(6000);

      expect(api['localCopilotReconnect']).toHaveBeenCalledTimes(2);
      expect(fixture.componentInstance.success()).toContain('каталог моделей оновлено');
    } finally {
      vi.useRealTimers();
    }
  });

  it.each([
    ['LoginFailed', 'Не вдалося завершити нову GitHub-авторизацію'],
    ['AuthorizationFailed', 'Не вдалося завершити GitHub-авторизацію'],
    ['RuntimeRestartFailed', 'runtime не вдалося перезапустити'],
    ['ValidationFailed', 'Copilot-сесію не вдалося перевірити'],
    ['ModelSyncFailed', 'не вдалося оновити каталог моделей'],
    ['Expired', 'Код входу застарів'],
  ] as const)('shows a safe terminal message for %s', async (state, expectedMessage) => {
    api['capabilities'].mockReturnValue(
      of({
        githubCopilotOAuthEnabled: false,
        githubCopilotPatEnabled: false,
        localCopilotCliEnabled: true,
      }),
    );
    api['localCopilotCliStatus'].mockReturnValue(
      of({ enabled: true, availableForCurrentUser: true, runtimeState: 'AuthorizationRequired' }),
    );
    api['startLocalCopilotReconnect'].mockReturnValue(
      of({
        operationId: 'operation-1',
        state,
        verificationUri: null,
        userCode: null,
        expiresAt: '2026-10-30T12:15:00Z',
        errorCode: state === 'Expired' ? 'DeviceCodeExpired' : state,
      }),
    );
    const fixture = await createFixture();

    await fixture.componentInstance.connectLocalCopilotCli();

    expect(fixture.componentInstance.error()).toContain(expectedMessage);
    expect(fixture.componentInstance.connectingLocalCli()).toBe(false);
  });

  it('shows Close instead of Cancel for a terminal reconnect state', async () => {
    api['capabilities'].mockReturnValue(
      of({
        githubCopilotOAuthEnabled: false,
        githubCopilotPatEnabled: false,
        localCopilotCliEnabled: true,
      }),
    );
    api['localCopilotCliStatus'].mockReturnValue(
      of({ enabled: true, availableForCurrentUser: true, runtimeState: 'Configured' }),
    );
    api['startLocalCopilotReconnect'].mockReturnValue(
      of({
        operationId: 'operation-1',
        state: 'ModelSyncFailed',
        verificationUri: null,
        userCode: null,
        expiresAt: '2026-10-30T12:15:00Z',
        errorCode: 'ModelCatalogUnavailable',
      }),
    );
    const fixture = await createFixture();

    await fixture.componentInstance.connectLocalCopilotCli();
    fixture.detectChanges();

    const close = fixture.nativeElement.querySelector(
      '[data-testid="local-copilot-reconnect-close"]',
    ) as HTMLButtonElement;
    expect(close.textContent).toContain('Закрити');
    close.click();
    expect(api['cancelLocalCopilotReconnect']).not.toHaveBeenCalled();
    expect(fixture.componentInstance.showLocalReconnect()).toBe(false);
  });

  it('clears the copied-code notice when a later stage fails', async () => {
    api['capabilities'].mockReturnValue(
      of({
        githubCopilotOAuthEnabled: false,
        githubCopilotPatEnabled: false,
        localCopilotCliEnabled: true,
      }),
    );
    api['localCopilotCliStatus'].mockReturnValue(
      of({ enabled: true, availableForCurrentUser: true, runtimeState: 'Configured' }),
    );
    api['localCopilotReconnect'].mockReturnValue(
      of({
        operationId: 'operation-1',
        state: 'ModelSyncFailed',
        verificationUri: null,
        userCode: null,
        expiresAt: '2026-10-30T12:15:00Z',
        errorCode: 'ModelCatalogUnavailable',
      }),
    );
    const fixture = await createFixture();
    vi.useFakeTimers();
    try {
      await fixture.componentInstance.connectLocalCopilotCli();
      await fixture.componentInstance.copyLocalCopilotCode();
      expect(fixture.componentInstance.success()).toBe('Код скопійовано.');

      await vi.advanceTimersByTimeAsync(1500);
      await Promise.resolve();

      expect(fixture.componentInstance.success()).toBe('');
      expect(fixture.componentInstance.error()).toContain('не вдалося оновити каталог моделей');
    } finally {
      vi.useRealTimers();
    }
  });

  it('submits PAT with expected login, clears the secret, and never renders it', async () => {
    const fixture = await createFixture();
    fixture.componentInstance.showGitHubPatForm.set(true);
    fixture.componentInstance.githubPatForm.setValue({
      expectedGitHubLogin: '@expected-user',
      personalAccessToken: 'github_pat_write-only-secret',
    });

    await fixture.componentInstance.connectGitHubPat();
    fixture.detectChanges();

    expect(api['connectGitHubPat']).toHaveBeenCalledWith(
      'github_pat_write-only-secret',
      'expected-user',
    );
    expect(fixture.componentInstance.githubPatForm.controls.personalAccessToken.value).toBe('');
    expect(fixture.componentInstance.showGitHubPatForm()).toBe(false);
    expect(fixture.nativeElement.textContent).not.toContain('github_pat_write-only-secret');
  });

  it('accepts a hyphenated login and trims surrounding paste whitespace before submission', async () => {
    const fixture = await createFixture();
    fixture.componentInstance.showGitHubPatForm.set(true);
    fixture.componentInstance.githubPatForm.setValue({
      expectedGitHubLogin: '  @student-user  ',
      personalAccessToken: ' \r\ngithub_pat_fictional_opaque_value_1234567890\r\n ',
    });

    await fixture.componentInstance.connectGitHubPat();

    expect(api['connectGitHubPat']).toHaveBeenCalledWith(
      'github_pat_fictional_opaque_value_1234567890',
      'student-user',
    );
  });

  it('shows a typed PAT error returned by Integrations API', async () => {
    api['connectGitHubPat'].mockReturnValue(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 422,
            error: {
              code: 'UnsupportedGitHubCredentialType',
              title: 'Safe backend title.',
            },
          }),
      ),
    );
    const fixture = await createFixture();
    fixture.componentInstance.githubPatForm.setValue({
      expectedGitHubLogin: 'student-user',
      personalAccessToken: 'github_pat_fictional_opaque_value_1234567890',
    });

    await fixture.componentInstance.connectGitHubPat();

    expect(fixture.componentInstance.error()).toBe(
      'Підтримується лише GitHub fine-grained PAT із префіксом github_pat_.',
    );
  });

  it('shows a specific local error for a prefix without an opaque token value', async () => {
    const fixture = await createFixture();
    fixture.componentInstance.showGitHubPatForm.set(true);
    fixture.componentInstance.githubPatForm.setValue({
      expectedGitHubLogin: 'student-user',
      personalAccessToken: 'github_pat_',
    });

    await fixture.componentInstance.connectGitHubPat();
    fixture.detectChanges();

    expect(api['connectGitHubPat']).not.toHaveBeenCalled();
    expect(fixture.nativeElement.textContent).toContain('Після префікса');
  });

  it('keeps credential replacement disabled until an eligible connection is selected', async () => {
    const fixture = await createFixture();
    const button = fixture.nativeElement.querySelector(
      '[data-testid="replace-credential"]',
    ) as HTMLButtonElement;

    expect(button.disabled).toBe(true);

    fixture.componentInstance.replaceForm.controls.connectionId.setValue('connection-1');
    fixture.componentInstance.replaceForm.controls.apiKey.setValue('replacement-secret');
    fixture.detectChanges();
    expect(button.disabled).toBe(false);
  });

  it('excludes Deterministic Test and GitHub OAuth connections from API-key rotation', async () => {
    api['list'].mockReturnValue(
      of([
        connection({ provider: 'DeterministicTest', credentialFingerprint: null }),
        connection({ id: 'github-1', provider: 'GitHubCopilot' }),
      ]),
    );
    const fixture = await createFixture();

    const options = Array.from(
      fixture.nativeElement.querySelectorAll(
        '#replace-connection option',
      ) as NodeListOf<HTMLOptionElement>,
    );
    expect(options).toHaveLength(1);
    expect(options[0]?.value).toBe('');
  });

  it('clears a create credential and never renders the raw secret', async () => {
    const fixture = await createFixture();
    fixture.componentInstance.form.setValue({
      provider: 'OpenAi',
      displayName: 'Personal OpenAI',
      apiKey: 'raw-secret-value',
    });

    await fixture.componentInstance.create();
    fixture.detectChanges();

    expect(api['create']).toHaveBeenCalledWith('OpenAi', 'Personal OpenAI', 'raw-secret-value');
    expect(fixture.componentInstance.form.controls.apiKey.value).toBe('');
    expect(fixture.nativeElement.textContent).not.toContain('raw-secret-value');
    expect(fixture.nativeElement.textContent).not.toContain('replacement-secret');
  });

  it('prevents duplicate create submissions while the request is pending', async () => {
    const pending = new Subject<ProviderConnectionDto>();
    api['create'].mockReturnValue(pending);
    const fixture = await createFixture();
    fixture.componentInstance.form.setValue({
      provider: 'OpenAi',
      displayName: 'Personal OpenAI',
      apiKey: 'write-only-secret',
    });

    const first = fixture.componentInstance.create();
    const duplicate = fixture.componentInstance.create();

    expect(api['create']).toHaveBeenCalledTimes(1);
    expect(fixture.componentInstance.creating()).toBe(true);

    pending.next(connection());
    pending.complete();
    await Promise.all([first, duplicate]);
    expect(fixture.componentInstance.creating()).toBe(false);
  });

  it('clears a replacement credential and displays a success status after rotation', async () => {
    const fixture = await createFixture();
    fixture.componentInstance.replaceForm.setValue({
      connectionId: 'connection-1',
      apiKey: 'replacement-secret',
    });

    await fixture.componentInstance.replaceCredential();
    fixture.detectChanges();

    expect(api['replaceCredential']).toHaveBeenCalledWith('connection-1', 'replacement-secret');
    expect(fixture.componentInstance.replaceForm.controls.apiKey.value).toBe('');
    expect(fixture.nativeElement.querySelector('[role="status"]')?.textContent).toContain(
      'API-ключ замінено',
    );
    expect(fixture.nativeElement.textContent).not.toContain('replacement-secret');
  });

  it.each([401, 403, 404, 409, 429, 503])('shows a safe error for HTTP %s', async (status) => {
    api['validate'].mockReturnValue(
      throwError(
        () =>
          new HttpErrorResponse({
            status,
            error: { title: `Request rejected (${status})` },
          }),
      ),
    );
    const fixture = await createFixture();

    await fixture.componentInstance.validate(connection());
    fixture.detectChanges();

    expect(fixture.componentInstance.error()).not.toBe('');
    expect(fixture.componentInstance.error()).not.toContain('secret');
    expect(fixture.nativeElement.querySelector('[role="alert"]')).not.toBeNull();
  });

  it('shows the safe provider validation reason returned by the backend', async () => {
    api['validate'].mockReturnValue(
      of(
        connection({
          status: 'Invalid',
          lastErrorCategory: 'Entitlement',
          lastSafeError: 'Для цього GitHub-акаунта недоступна підписка Copilot.',
        }),
      ),
    );
    const fixture = await createFixture();

    await fixture.componentInstance.validate(connection());
    fixture.detectChanges();

    expect(fixture.componentInstance.error()).toContain('недоступна підписка Copilot');
    expect(fixture.componentInstance.success()).toBe('');
  });

  it('shows the connected GitHub login without exposing a credential', async () => {
    api['list'].mockReturnValue(
      of([
        connection({
          provider: 'GitHubCopilot',
          externalAccountId: '12345',
          externalAccountLogin: 'student-user',
        }),
      ]),
    );
    const fixture = await createFixture();

    expect(
      fixture.nativeElement.querySelector('[data-testid="github-account"]')?.textContent,
    ).toContain('@student-user');
    expect(fixture.nativeElement.textContent).not.toContain('current-user-oauth-token');
    expect(fixture.nativeElement.textContent).toContain('Версія credential');
  });

  it('does not invent a Local CLI credential expiry when the runtime reports none', async () => {
    enableLocalCopilot();
    api['list'].mockReturnValue(
      of([
        connection({
          provider: 'GitHubCopilotLocalCli',
          externalAccountLogin: 'student-user',
          credentialExpiresAt: null,
        }),
      ]),
    );

    const fixture = await createFixture();

    expect(fixture.nativeElement.textContent).toContain(
      'Точний строк дії недоступний. Copilot CLI автоматично оновлює короткоживучі токени',
    );
    expect(fixture.nativeElement.textContent).not.toContain('Expiring soon');
  });

  it('shows a provider-reported Local CLI expiry and an expiring-soon warning', async () => {
    enableLocalCopilot();
    const expiresAt = new Date(Date.now() + 3 * 24 * 60 * 60 * 1000).toISOString();
    api['list'].mockReturnValue(
      of([
        connection({
          provider: 'GitHubCopilotLocalCli',
          externalAccountLogin: 'student-user',
          credentialExpiresAt: expiresAt,
          expirationSource: 'ProviderReported',
        }),
      ]),
    );

    const fixture = await createFixture();
    const expiration = fixture.nativeElement.querySelector(
      '[data-testid="local-copilot-expiration"]',
    );

    expect(expiration?.textContent).toContain('Діє до');
    expect(expiration?.textContent).toContain('Expiring soon');
    expect(fixture.nativeElement.textContent).not.toContain(
      'Copilot CLI автоматично оновлює короткоживучі токени',
    );
  });

  it('shows unavailable provider usage instead of synthetic zeroes', async () => {
    api['usage'].mockReturnValue(
      of({
        requestCount: 1,
        inputTokens: 0,
        cachedInputTokens: null,
        outputTokens: 0,
        estimatedCostUsd: 0,
        since: '2026-10-01T00:00:00Z',
        usageSource: 'Unavailable',
        costSource: 'Unavailable',
      }),
    );

    const fixture = await createFixture();
    expect(
      fixture.nativeElement.querySelector('[data-testid="usage-input-tokens"]')?.textContent.trim(),
    ).toBe('Unavailable');
    expect(
      fixture.nativeElement
        .querySelector('[data-testid="usage-cached-tokens"]')
        ?.textContent.trim(),
    ).toBe('Unavailable');
    expect(
      fixture.nativeElement
        .querySelector('[data-testid="usage-output-tokens"]')
        ?.textContent.trim(),
    ).toBe('Unavailable');
    expect(
      fixture.nativeElement.querySelector('[data-testid="usage-cost"]')?.textContent.trim(),
    ).toBe('Unavailable');
  });

  it('keeps provider-confirmed usage zeroes as zeroes', async () => {
    api['usage'].mockReturnValue(
      of({
        requestCount: 0,
        inputTokens: 0,
        cachedInputTokens: 0,
        outputTokens: 0,
        estimatedCostUsd: 0,
        since: '2026-10-01T00:00:00Z',
        usageSource: 'ProviderReported',
        costSource: 'ProviderReported',
      }),
    );

    const fixture = await createFixture();
    expect(
      fixture.nativeElement.querySelector('[data-testid="usage-input-tokens"]')?.textContent.trim(),
    ).toBe('0');
    expect(
      fixture.nativeElement
        .querySelector('[data-testid="usage-cached-tokens"]')
        ?.textContent.trim(),
    ).toBe('0');
    expect(
      fixture.nativeElement
        .querySelector('[data-testid="usage-output-tokens"]')
        ?.textContent.trim(),
    ).toBe('0');
    expect(
      fixture.nativeElement.querySelector('[data-testid="usage-cost"]')?.textContent.trim(),
    ).toBe('0 USD');
  });

  function enableLocalCopilot(): void {
    api['capabilities'].mockReturnValue(
      of({
        githubCopilotOAuthEnabled: false,
        githubCopilotPatEnabled: false,
        localCopilotCliEnabled: true,
        localCopilotCliReauthenticationEnabled: false,
        localCopilotCliLogoutEnabled: false,
      }),
    );
    api['localCopilotCliStatus'].mockReturnValue(
      of({
        enabled: true,
        availableForCurrentUser: true,
        runtimeState: 'Ready',
        sidecarState: 'Ready',
        authenticationState: 'Active',
      }),
    );
  }

  async function createFixture(): Promise<ComponentFixture<ProviderConnectionsComponent>> {
    const fixture = TestBed.createComponent(ProviderConnectionsComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    await vi.waitFor(() => expect(fixture.componentInstance.loading()).toBe(false));
    fixture.detectChanges();
    return fixture;
  }
});

function connection(overrides: Partial<ProviderConnectionDto> = {}): ProviderConnectionDto {
  return {
    id: 'connection-1',
    provider: 'OpenAi',
    displayName: 'Personal OpenAI',
    status: 'Active',
    credentialFingerprint: 'sha256:abcd',
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
    ...overrides,
  };
}
