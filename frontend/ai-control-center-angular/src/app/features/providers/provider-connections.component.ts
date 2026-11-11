import { HttpErrorResponse } from '@angular/common/http';
import {
  Component,
  computed,
  inject,
  InjectionToken,
  OnDestroy,
  OnInit,
  signal,
} from '@angular/core';
import {
  AbstractControl,
  FormBuilder,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { directionErrorMessage } from '../directions/direction-error';
import { ProviderApiService } from './provider-api.service';
import {
  AiProviderKind,
  GitHubOAuthConfigurationStatus,
  LocalCopilotCliStatusDto,
  LocalCopilotReconnectDto,
  ProviderConnectionCapabilitiesDto,
  ProviderConnectionDto,
  ProviderModelDto,
  ProviderUsageSummaryDto,
} from './provider.models';

export const PROVIDER_OAUTH_NAVIGATOR = new InjectionToken<(url: string) => void>(
  'PROVIDER_OAUTH_NAVIGATOR',
  { providedIn: 'root', factory: () => (url) => globalThis.location.assign(url) },
);
export const PROVIDER_CLIPBOARD = new InjectionToken<(value: string) => Promise<void>>(
  'PROVIDER_CLIPBOARD',
  { providedIn: 'root', factory: () => (value) => globalThis.navigator.clipboard.writeText(value) },
);

const githubFineGrainedPatPrefix = 'github_pat_';
const maximumCredentialLength = 8192;

function githubLoginValidator(control: AbstractControl): ValidationErrors | null {
  const raw = typeof control.value === 'string' ? control.value : '';
  const trimmed = raw.trim();
  const login = trimmed.startsWith('@') ? trimmed.slice(1) : trimmed;
  if (!login) return { required: true };
  if (login.length > 100) return { maxlength: true };
  if (!/^[A-Za-z0-9-]+$/.test(login)) return { githubLoginFormat: true };
  return null;
}

function githubFineGrainedPatValidator(control: AbstractControl): ValidationErrors | null {
  const raw = typeof control.value === 'string' ? control.value : '';
  const value = raw.trim();
  if (!value) return { required: true };
  if (value.length > maximumCredentialLength) return { maxlength: true };
  if (!value.startsWith(githubFineGrainedPatPrefix)) return { unsupportedCredentialType: true };
  if (value.length === githubFineGrainedPatPrefix.length) return { missingTokenValue: true };
  if (
    Array.from(value).some((character) => {
      const code = character.charCodeAt(0);
      return /\s/.test(character) || code < 32 || code === 127;
    })
  )
    return { malformedCredential: true };
  return null;
}

function githubPatErrorMessage(error: unknown): string {
  if (!(error instanceof HttpErrorResponse)) return directionErrorMessage(error);
  const code = typeof error.error?.code === 'string' ? error.error.code : '';
  const messages: Record<string, string> = {
    UnsupportedGitHubCredentialType:
      'Підтримується лише GitHub fine-grained PAT із префіксом github_pat_.',
    InvalidExpectedGitHubLogin: 'Вкажіть коректний очікуваний GitHub login.',
    GitHubAccountMismatch: 'PAT належить іншому GitHub-акаунту; підключення не змінено.',
    GitHubIdentityUnavailable: 'GitHub не підтвердив власника PAT; підключення не змінено.',
    GitHubModelCatalogUnavailable:
      'GitHub не повернув доступний каталог моделей; PAT не збережено.',
    InvalidCredential: 'GitHub PAT недійсний або відкликаний.',
    RequiresReconnect: 'GitHub PAT недійсний або відкликаний.',
    NotEntitled: 'Для підтвердженого GitHub-акаунта Copilot недоступний.',
    PolicyBlocked: 'GitHub Copilot заблоковано політикою акаунта або організації.',
    OAuthAppUnsupported: 'GitHub не прийняв цей тип credential для Copilot.',
    InsufficientPermissions: 'Fine-grained PAT не має дозволу Copilot Requests.',
    RateLimited: 'GitHub тимчасово обмежив кількість запитів; PAT не збережено.',
    TemporarilyUnavailable: 'GitHub Copilot тимчасово недоступний; PAT не збережено.',
    QuotaExhausted: 'GitHub Copilot quota вичерпана; PAT не збережено.',
  };
  if (messages[code]) return messages[code];
  if (error.status === 422 && typeof error.error?.title === 'string') return error.error.title;
  return directionErrorMessage(error);
}

function localCopilotErrorMessage(error: unknown): string {
  if (!(error instanceof HttpErrorResponse)) return directionErrorMessage(error);
  const code = typeof error.error?.code === 'string' ? error.error.code : '';
  const traceId = typeof error.error?.traceId === 'string' ? error.error.traceId : '';
  const messages: Record<string, string> = {
    SidecarUnavailable: 'Local Copilot sidecar недоступний.',
    RuntimeUnavailable: 'Local Copilot runtime недоступний.',
    Unauthenticated: 'Local Copilot CLI потребує авторизації.',
    InsufficientPermissions: 'Недостатньо прав для Local Copilot CLI.',
    ValidationTimeout: 'Перевірка Local Copilot CLI перевищила час очікування.',
    ModelCatalogUnavailable: 'Каталог моделей Local Copilot CLI недоступний.',
    ConfigurationError: 'Конфігурація Local Copilot CLI некоректна.',
    RateLimited: 'Забагато запитів. Повторна перевірка буде виконана автоматично.',
    LocalCopilotReauthenticationUnsupported:
      'Поточна bundled версія Copilot CLI не підтримує безпечну зміну акаунта.',
    LocalCopilotLogoutUnsupported:
      'Поточна bundled версія Copilot CLI не підтримує стабільний logout.',
  };
  const message =
    messages[code] ??
    (typeof error.error?.title === 'string'
      ? error.error.title
      : 'Не вдалося перевірити Local Copilot CLI.');
  return traceId ? `${message} Correlation ID: ${traceId}` : message;
}

function localCopilotOperationError(operation: LocalCopilotReconnectDto): string {
  const code = operation.errorCode ?? '';
  const messages: Record<string, string> = {
    CopilotLoginFailed: 'Copilot CLI завершив device login з помилкою.',
    CredentialNotPersisted: 'Copilot CLI не підтвердив збереження нової локальної сесії.',
    DeviceCodeExpired: 'Код входу застарів. Запустіть підключення ще раз.',
    RuntimeRestartFailed: 'Local Copilot runtime не вдалося перезапустити.',
    ValidationTimeout: 'Перевірка Local Copilot CLI перевищила час очікування.',
    ModelCatalogUnavailable: 'не вдалося оновити каталог моделей Local Copilot CLI.',
    RateLimited: 'Забагато запитів до Local Copilot CLI.',
  };
  const stateMessages: Partial<Record<LocalCopilotReconnectDto['state'], string>> = {
    LoginFailed: 'Не вдалося завершити нову GitHub-авторизацію Local Copilot CLI.',
    AuthorizationFailed: 'Не вдалося завершити GitHub-авторизацію Local Copilot CLI.',
    RuntimeRestartFailed: 'Local Copilot runtime не вдалося перезапустити.',
    ValidationFailed: 'Copilot-сесію не вдалося перевірити.',
    ModelSyncFailed: 'не вдалося оновити каталог моделей Local Copilot CLI.',
    Expired: 'Код входу застарів. Запустіть підключення ще раз.',
  };
  const message =
    messages[code] ??
    stateMessages[operation.state] ??
    'Local Copilot operation завершилася з помилкою.';
  return operation.correlationId
    ? `${message} Correlation ID: ${operation.correlationId}`
    : message;
}

@Component({
  selector: 'app-provider-connections',
  imports: [ReactiveFormsModule],
  styleUrl: './provider-connections.component.scss',
  templateUrl: './provider-connections.component.html',
})
export class ProviderConnectionsComponent implements OnInit, OnDestroy {
  private readonly api = inject(ProviderApiService);
  private readonly fb = inject(FormBuilder);
  private readonly navigateToOAuth = inject(PROVIDER_OAUTH_NAVIGATOR);
  private readonly copyToClipboard = inject(PROVIDER_CLIPBOARD);
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;

  readonly connections = signal<ProviderConnectionDto[]>([]);
  readonly models = signal<ProviderModelDto[]>([]);
  readonly selected = signal<string | null>(null);
  readonly loading = signal(true);
  readonly creating = signal(false);
  readonly replacing = signal(false);
  readonly connectingGitHub = signal(false);
  readonly connectingGitHubPat = signal(false);
  readonly connectingLocalCli = signal(false);
  readonly showGitHubPatForm = signal(false);
  readonly githubOAuthStatus = signal<GitHubOAuthConfigurationStatus | 'Loading'>('Loading');
  readonly localCliStatus = signal<LocalCopilotCliStatusDto | null>(null);
  readonly capabilities = signal<ProviderConnectionCapabilitiesDto>({
    githubCopilotOAuthEnabled: false,
    githubCopilotPatEnabled: false,
    localCopilotCliEnabled: false,
    localCopilotCliReauthenticationEnabled: false,
    localCopilotCliLogoutEnabled: false,
  });
  readonly localReconnect = signal<LocalCopilotReconnectDto | null>(null);
  readonly showLocalReconnect = signal(false);
  readonly showLocalReauthenticationConfirmation = signal(false);
  readonly showLocalLogoutConfirmation = signal(false);
  readonly actionId = signal<string | null>(null);
  readonly error = signal('');
  readonly success = signal('');
  readonly usage = signal<ProviderUsageSummaryDto | null>(null);
  readonly replaceableConnections = computed(() =>
    this.connections().filter(
      (item) =>
        (item.provider === 'OpenAi' || item.provider === 'XAi') && item.status !== 'Revoked',
    ),
  );

  readonly form = this.fb.nonNullable.group({
    provider: ['DeterministicTest' as AiProviderKind, Validators.required],
    displayName: [
      'Local deterministic provider',
      [Validators.required, Validators.minLength(2), Validators.maxLength(100)],
    ],
    apiKey: ['', Validators.maxLength(8192)],
  });
  readonly replaceForm = this.fb.nonNullable.group({
    connectionId: ['', Validators.required],
    apiKey: ['', [Validators.required, Validators.maxLength(8192)]],
  });
  readonly githubPatForm = this.fb.nonNullable.group({
    expectedGitHubLogin: ['', githubLoginValidator],
    personalAccessToken: ['', githubFineGrainedPatValidator],
  });

  ngOnInit() {
    void this.reload();
  }

  ngOnDestroy(): void {
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer);
  }

  providerRequiresApiKey(): boolean {
    const provider = this.form.controls.provider.value;
    return provider === 'OpenAi' || provider === 'XAi';
  }

  providerChanged(): void {
    const control = this.form.controls.apiKey;
    control.setValidators(
      this.providerRequiresApiKey()
        ? [Validators.required, Validators.maxLength(8192)]
        : [Validators.maxLength(8192)],
    );
    if (!this.providerRequiresApiKey()) control.setValue('');
    control.updateValueAndValidity();
  }

  async create() {
    if (this.creating()) return;
    this.providerChanged();
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const value = this.form.getRawValue();
    this.creating.set(true);
    this.clearFeedback();
    try {
      await firstValueFrom(
        this.api.create(value.provider, value.displayName, value.apiKey.trim() || null),
      );
      this.form.controls.apiKey.setValue('');
      this.success.set(`Провайдера «${value.displayName}» додано.`);
      await this.reload(false);
    } catch (error) {
      this.error.set(directionErrorMessage(error));
    } finally {
      this.creating.set(false);
    }
  }

  async validate(item: ProviderConnectionDto) {
    if (this.actionId()) return;
    this.actionId.set(`validate:${item.id}`);
    this.clearFeedback();
    try {
      const result = await firstValueFrom(this.api.validate(item.id));
      await this.reload(false);
      if (result.lastSafeError) this.error.set(result.lastSafeError);
      else this.success.set(`Підключення «${item.displayName}» перевірено.`);
    } catch (error) {
      this.error.set(directionErrorMessage(error));
    } finally {
      this.actionId.set(null);
    }
  }

  async replaceCredential() {
    if (this.replacing()) return;
    if (this.replaceForm.invalid) {
      this.replaceForm.markAllAsTouched();
      return;
    }

    const value = this.replaceForm.getRawValue();
    this.replacing.set(true);
    this.clearFeedback();
    try {
      await firstValueFrom(this.api.replaceCredential(value.connectionId, value.apiKey));
      this.replaceForm.reset();
      this.success.set('API-ключ замінено. Попереднє значення більше не використовується.');
      await this.reload(false);
    } catch (error) {
      this.error.set(directionErrorMessage(error));
    } finally {
      this.replacing.set(false);
    }
  }

  async loadModels(item: ProviderConnectionDto) {
    if (this.actionId()) return;
    this.actionId.set(`models:${item.id}`);
    this.clearFeedback();
    try {
      this.selected.set(item.id);
      this.models.set(await firstValueFrom(this.api.models(item.id)));
      this.success.set(`Каталог моделей «${item.displayName}» оновлено.`);
    } catch (error) {
      this.error.set(directionErrorMessage(error));
    } finally {
      this.actionId.set(null);
    }
  }

  async revoke(item: ProviderConnectionDto) {
    if (this.actionId()) return;
    this.actionId.set(`revoke:${item.id}`);
    this.clearFeedback();
    try {
      await firstValueFrom(this.api.revoke(item.id));
      if (this.selected() === item.id) {
        this.selected.set(null);
        this.models.set([]);
      }
      this.success.set(`Підключення «${item.displayName}» відкликано.`);
      await this.reload(false);
    } catch (error) {
      this.error.set(directionErrorMessage(error));
    } finally {
      this.actionId.set(null);
    }
  }

  async connectGitHub() {
    if (this.connectingGitHub() || this.githubOAuthStatus() !== 'Available') {
      if (this.githubOAuthStatus() === 'NotConfigured')
        this.error.set('GitHub OAuth не налаштовано');
      return;
    }
    this.connectingGitHub.set(true);
    this.clearFeedback();
    try {
      const result = await firstValueFrom(this.api.githubOAuthStart());
      if (result.status !== 'Available' || !result.authorizationUri) {
        this.githubOAuthStatus.set('NotConfigured');
        this.error.set('GitHub OAuth не налаштовано');
        return;
      }
      this.navigateToOAuth(result.authorizationUri);
    } catch (error) {
      this.error.set(directionErrorMessage(error));
    } finally {
      this.connectingGitHub.set(false);
    }
  }

  toggleGitHubPatForm(): void {
    this.clearFeedback();
    this.showGitHubPatForm.update((visible) => !visible);
    if (!this.showGitHubPatForm()) this.githubPatForm.reset();
  }

  async connectGitHubPat(): Promise<void> {
    if (this.connectingGitHubPat()) return;
    if (this.githubPatForm.invalid) {
      this.githubPatForm.markAllAsTouched();
      return;
    }

    const value = this.githubPatForm.getRawValue();
    const expectedLogin = value.expectedGitHubLogin.trim().replace(/^@/, '');
    const pat = value.personalAccessToken.trim();
    this.connectingGitHubPat.set(true);
    this.clearFeedback();
    try {
      await firstValueFrom(this.api.connectGitHubPat(pat, expectedLogin));
      this.githubPatForm.reset();
      this.showGitHubPatForm.set(false);
      this.success.set('GitHub Copilot підключено через fine-grained PAT.');
      await this.reload(false);
    } catch (error) {
      this.githubPatForm.controls.personalAccessToken.setValue('');
      this.error.set(githubPatErrorMessage(error));
    } finally {
      this.connectingGitHubPat.set(false);
    }
  }

  async connectLocalCopilotCli(): Promise<void> {
    if (this.connectingLocalCli() || !this.localCliStatus()?.availableForCurrentUser) return;
    this.connectingLocalCli.set(true);
    this.clearFeedback();
    try {
      const operation = await firstValueFrom(this.api.startLocalCopilotReconnect());
      this.localReconnect.set(operation);
      this.showLocalReconnect.set(true);
      this.scheduleReconnectPoll(operation);
    } catch (error) {
      this.error.set(localCopilotErrorMessage(error));
      this.connectingLocalCli.set(false);
    }
  }

  openLocalCopilotReauthenticationConfirmation(): void {
    if (
      this.connectingLocalCli() ||
      !this.capabilities().localCopilotCliReauthenticationEnabled ||
      (!this.localCopilotCredentialReady() && !this.localCopilotCredentialNeedsReauthentication())
    )
      return;
    this.clearFeedback();
    this.showLocalReauthenticationConfirmation.set(true);
  }

  cancelLocalCopilotReauthentication(): void {
    this.showLocalReauthenticationConfirmation.set(false);
  }

  async confirmLocalCopilotReauthentication(): Promise<void> {
    if (this.connectingLocalCli() || !this.showLocalReauthenticationConfirmation()) return;
    this.showLocalReauthenticationConfirmation.set(false);
    this.connectingLocalCli.set(true);
    this.clearFeedback();
    try {
      const operation = await firstValueFrom(this.api.startLocalCopilotReauthentication());
      this.localReconnect.set(operation);
      this.showLocalReconnect.set(true);
      this.scheduleReconnectPoll(operation);
    } catch (error) {
      this.error.set(localCopilotErrorMessage(error));
      this.connectingLocalCli.set(false);
    }
  }

  openLocalCopilotLogoutConfirmation(): void {
    if (this.connectingLocalCli() || !this.localCopilotCredentialReady()) return;
    this.clearFeedback();
    this.showLocalLogoutConfirmation.set(true);
  }

  cancelLocalCopilotLogout(): void {
    this.showLocalLogoutConfirmation.set(false);
  }

  async confirmLocalCopilotLogout(): Promise<void> {
    if (this.connectingLocalCli() || !this.showLocalLogoutConfirmation()) return;
    this.showLocalLogoutConfirmation.set(false);
    this.connectingLocalCli.set(true);
    this.clearFeedback();
    try {
      const result = await firstValueFrom(this.api.logoutLocalCopilotSession());
      if (result.status !== 'Succeeded' && result.status !== 'AlreadyUnauthenticated') {
        this.error.set('Не вдалося завершити локальну сесію Copilot CLI.');
      } else {
        this.success.set('Локальну сесію Copilot CLI завершено.');
      }
      await this.reload(false);
    } catch (error) {
      this.error.set(localCopilotErrorMessage(error));
    } finally {
      this.connectingLocalCli.set(false);
    }
  }

  async cancelLocalCopilotReconnect(): Promise<void> {
    const operation = this.localReconnect();
    if (!operation) return;
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer);
    try {
      this.localReconnect.set(
        await firstValueFrom(this.api.cancelLocalCopilotReconnect(operation.operationId)),
      );
    } catch (error) {
      this.error.set(localCopilotErrorMessage(error));
    } finally {
      this.connectingLocalCli.set(false);
      this.showLocalReconnect.set(false);
    }
  }

  closeLocalCopilotReconnect(): void {
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer);
    this.connectingLocalCli.set(false);
    this.showLocalReconnect.set(false);
  }

  async copyLocalCopilotCode(): Promise<void> {
    const code = this.localReconnect()?.userCode;
    if (!code) return;
    await this.copyToClipboard(code);
    this.success.set('Код скопійовано.');
  }

  localCopilotActionLabel(): string {
    if (this.connectingLocalCli()) return 'Перевірка…';
    return this.localCopilotCredentialReady()
      ? 'Перевірити підключення'
      : 'Підключити Local Copilot CLI';
  }

  localCopilotCredentialReady(): boolean {
    return this.localCliStatus()?.runtimeState === 'Ready';
  }

  localCopilotModelsState(): string {
    const connection = this.localCopilotConnection();
    return connection?.lastSyncedAt ? 'Synced' : 'Unavailable';
  }

  localCopilotConnection(): ProviderConnectionDto | undefined {
    return this.connections().find(
      (item) => item.provider === 'GitHubCopilotLocalCli' && item.status !== 'Revoked',
    );
  }

  localCopilotCredentialNeedsReauthentication(): boolean {
    const connection = this.localCopilotConnection();
    return connection?.status === 'RequiresReconnect' || connection?.status === 'Invalid';
  }

  localReconnectIsTerminal(state: LocalCopilotReconnectDto['state']): boolean {
    return (
      state === 'Authorized' ||
      state === 'Cancelled' ||
      state === 'LoginFailed' ||
      state === 'AuthorizationFailed' ||
      state === 'RuntimeRestartFailed' ||
      state === 'ValidationFailed' ||
      state === 'ModelSyncFailed' ||
      state === 'Expired'
    );
  }

  localReconnectStageMessage(operation: LocalCopilotReconnectDto): string {
    const messages: Record<LocalCopilotReconnectDto['state'], string> = {
      Starting: 'Готуємо Local Copilot CLI…',
      StartingLogin: 'Запускаємо нову GitHub-авторизацію…',
      WaitingForUser: 'Очікуємо підтвердження на GitHub.',
      GitHubAuthorized: 'GitHub-авторизацію підтверджено.',
      PersistingCredential: 'Зберігаємо підтверджену Copilot-сесію…',
      RestartingRuntime: 'Перезапускаємо Local Copilot runtime…',
      Validating: 'Перевіряємо авторизовану Copilot-сесію…',
      SynchronizingModels: 'Оновлюємо каталог доступних моделей…',
      Cancelling: 'Безпечно зупиняємо поточну операцію…',
      Authorized: 'Local Copilot CLI готовий до роботи.',
      LoginFailed: 'Не вдалося завершити нову GitHub-авторизацію Local Copilot CLI.',
      AuthorizationFailed: 'Не вдалося завершити GitHub-авторизацію Local Copilot CLI.',
      RuntimeRestartFailed: 'GitHub-авторизацію завершено, але runtime не вдалося перезапустити.',
      ValidationFailed: 'GitHub-авторизацію завершено, але Copilot-сесію не вдалося перевірити.',
      ModelSyncFailed: 'GitHub-авторизацію завершено, але не вдалося оновити каталог моделей.',
      Expired: 'Код входу застарів. Запустіть відновлення ще раз.',
      Cancelled: 'Відновлення Local Copilot CLI скасовано.',
    };
    return messages[operation.state];
  }

  modelsFor(item: ProviderConnectionDto): ProviderModelDto[] {
    return this.selected() === item.id ? this.models() : [];
  }

  providerLabel(provider: AiProviderKind): string {
    const labels: Record<AiProviderKind, string> = {
      DeterministicTest: 'Deterministic Test',
      GitHubCopilot: 'GitHub Copilot',
      GitHubCopilotLocalCli: 'Local Copilot CLI',
      OpenAi: 'OpenAI',
      XAi: 'xAI',
    };
    return labels[provider];
  }

  formatDate(value: string | null): string {
    return value
      ? new Intl.DateTimeFormat('uk-UA', { dateStyle: 'medium', timeStyle: 'short' }).format(
          new Date(value),
        )
      : 'ще не виконувалась';
  }

  usageValue(value: number | null, source: string): string {
    return source === 'Unavailable' || value === null ? 'Unavailable' : String(value);
  }

  costValue(value: number | null, source: string): string {
    return source === 'Unavailable' || value === null ? 'Unavailable' : `${value} USD`;
  }

  credentialExpiringSoon(value: string | null | undefined): boolean {
    if (!value) return false;
    const expiresAt = Date.parse(value);
    if (!Number.isFinite(expiresAt)) return false;
    const remaining = expiresAt - Date.now();
    return remaining > 0 && remaining <= 7 * 24 * 60 * 60 * 1000;
  }

  private clearFeedback(): void {
    this.error.set('');
    this.success.set('');
  }

  private scheduleReconnectPoll(operation: LocalCopilotReconnectDto): void {
    if (operation.state !== 'WaitingForUser') this.success.set('');
    if (operation.state === 'Authorized') {
      this.connectingLocalCli.set(false);
      this.showLocalReconnect.set(false);
      this.success.set('Local Copilot CLI авторизовано, підключення та каталог моделей оновлено.');
      void this.reload(false);
      return;
    }
    if (this.localReconnectIsTerminal(operation.state)) {
      this.connectingLocalCli.set(false);
      if (operation.state !== 'Cancelled') this.error.set(localCopilotOperationError(operation));
      return;
    }
    this.reconnectTimer = setTimeout(async () => {
      try {
        const current = await firstValueFrom(this.api.localCopilotReconnect(operation.operationId));
        if (this.localReconnect()?.operationId !== current.operationId) return;
        this.localReconnect.set(current);
        this.scheduleReconnectPoll(current);
      } catch (error) {
        this.error.set(localCopilotErrorMessage(error));
        if (
          error instanceof HttpErrorResponse &&
          [429, 502, 503, 504].includes(error.status) &&
          Date.now() < new Date(operation.expiresAt).getTime()
        ) {
          this.reconnectTimer = setTimeout(() => this.scheduleReconnectPoll(operation), 2500);
          return;
        }
        this.connectingLocalCli.set(false);
      }
    }, 1500);
  }

  private async reload(showLoading = true) {
    if (showLoading) this.loading.set(true);
    try {
      const capabilities = await firstValueFrom(this.api.capabilities());
      this.capabilities.set(capabilities);
      const [connections, usage, githubOAuth, localCli] = await Promise.all([
        firstValueFrom(this.api.list()),
        firstValueFrom(this.api.usage()),
        capabilities.githubCopilotOAuthEnabled
          ? firstValueFrom(this.api.githubOAuthStatus())
          : Promise.resolve({ status: 'NotConfigured' as const }),
        capabilities.localCopilotCliEnabled
          ? firstValueFrom(this.api.localCopilotCliStatus())
          : Promise.resolve(null),
      ]);
      this.connections.set(connections);
      this.usage.set(usage);
      this.githubOAuthStatus.set(githubOAuth.status);
      this.localCliStatus.set(localCli);
      this.error.set('');
      if (localCli?.availableForCurrentUser) {
        const current = await firstValueFrom(this.api.currentLocalCopilotReconnect());
        if (current && !this.localReconnectIsTerminal(current.state)) {
          if (this.localReconnect()?.operationId !== current.operationId) {
            if (this.reconnectTimer) clearTimeout(this.reconnectTimer);
            this.localReconnect.set(current);
            this.showLocalReconnect.set(true);
            this.connectingLocalCli.set(true);
            this.scheduleReconnectPoll(current);
          }
        }
      }
    } catch (error) {
      this.error.set(directionErrorMessage(error));
    } finally {
      this.loading.set(false);
    }
  }
}
