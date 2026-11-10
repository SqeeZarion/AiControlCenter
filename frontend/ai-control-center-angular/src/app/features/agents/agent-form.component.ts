import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { DirectionApiService } from '../directions/direction-api.service';
import { directionErrorMessage } from '../directions/direction-error';
import { DirectionDto } from '../directions/direction.models';
import { AgentApiService } from './agent-api.service';
import { AgentDefinitionDto, AgentExecutionType, AgentStatus } from './agent.models';
import { ProviderApiService } from '../providers/provider-api.service';
import {
  LocalCopilotCliStatusDto,
  ProviderConnectionCapabilitiesDto,
  ProviderConnectionDto,
  ProviderModelDto,
} from '../providers/provider.models';
import { agentErrorMessage } from './agent-error';

type RoutingPreset =
  | 'GitHubOnly'
  | 'LocalCopilotCli'
  | 'OpenAIOnly'
  | 'GrokOnly'
  | 'GitHubThenOpenAI'
  | 'GitHubThenGrok';

interface ModelRouteDraft {
  connectionId: string;
  connectionName: string;
  provider: string;
  modelId: string;
  priority: number;
  enabled: boolean;
  allowedFallbackReasons: string[];
  maxAttempts: number;
  timeoutSeconds: number;
  paidExecutionAllowed: boolean;
  paidFallbackAllowed: boolean;
  safetyCapUsd?: number;
  dailyCostCapUsd?: number;
  monthlyCostCapUsd?: number;
  maxRequestsPerDay?: number;
  maxRequestsPerMonth?: number;
}

@Component({
  selector: 'app-agent-form',
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './agent-form.component.html',
  styleUrl: '../stage4.scss',
})
export class AgentFormComponent implements OnInit {
  private readonly api = inject(AgentApiService);
  private readonly directionsApi = inject(DirectionApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly fb = inject(FormBuilder);
  private readonly providersApi = inject(ProviderApiService);
  private agent?: AgentDefinitionDto;
  readonly directions = signal<DirectionDto[]>([]);
  readonly providerConnections = signal<ProviderConnectionDto[]>([]);
  readonly providerCapabilities = signal<ProviderConnectionCapabilitiesDto | null>(null);
  readonly localCliStatus = signal<LocalCopilotCliStatusDto | null>(null);
  readonly routeModels = signal<ProviderModelDto[]>([]);
  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly error = signal('');
  readonly routeError = signal('');
  readonly isEdit = signal(false);
  readonly form = this.fb.nonNullable.group({
    directionId: ['', Validators.required],
    name: ['', [Validators.required, Validators.minLength(2), Validators.maxLength(100)]],
    code: [
      '',
      [
        Validators.required,
        Validators.minLength(2),
        Validators.maxLength(64),
        Validators.pattern(/^[a-zA-Z0-9][a-zA-Z0-9 _-]*$/),
      ],
    ],
    description: ['', Validators.maxLength(1000)],
    status: ['Active' as AgentStatus, Validators.required],
    executionType: ['Test' as AgentExecutionType, Validators.required],
    systemPrompt: ['', Validators.maxLength(16000)],
    modelRoutesJson: ['[]', Validators.required],
    toolPolicyJson: ['[]', Validators.required],
    maxInputTokens: [8000, [Validators.required, Validators.min(1)]],
    maxOutputTokens: [2000, [Validators.required, Validators.min(1)]],
    maxEstimatedCostUsd: [1, [Validators.required, Validators.min(0.000001)]],
    maxAttempts: [3, [Validators.required, Validators.min(1), Validators.max(10)]],
  });
  readonly routeForm = this.fb.nonNullable.group({
    connectionId: ['', Validators.required],
    modelId: ['Auto', Validators.required],
    maxAttempts: [1, [Validators.required, Validators.min(1), Validators.max(3)]],
    timeoutSeconds: [60, [Validators.required, Validators.min(1), Validators.max(300)]],
    paidExecutionAllowed: [false],
    paidFallbackAllowed: [false],
    safetyCapUsd: [1, Validators.min(0.000001)],
    dailyCostCapUsd: [10, Validators.min(0.000001)],
    monthlyCostCapUsd: [100, Validators.min(0.000001)],
    maxRequestsPerDay: [20, [Validators.min(1), Validators.max(1000)]],
    maxRequestsPerMonth: [200, [Validators.min(1), Validators.max(10000)]],
  });
  readonly activeConnections = computed(() =>
    this.providerConnections().filter((connection) => this.connectionEligible(connection)),
  );
  selectedConnection(): ProviderConnectionDto | undefined {
    return this.providerConnections().find(
      (connection) => connection.id === this.routeForm.controls.connectionId.value,
    );
  }

  ngOnInit(): void {
    void this.initialize();
  }

  async submit(): Promise<void> {
    this.form.markAllAsTouched();
    if (this.form.controls.executionType.value === 'Ai' && this.routes().length === 0) {
      this.routeError.set('Додайте щонайменше один доступний маршрут моделі.');
      return;
    }
    if (this.form.invalid || this.saving()) return;
    this.saving.set(true);
    this.error.set('');
    try {
      const value = this.form.getRawValue();
      const ai = value.executionType === 'Ai';
      const content = {
        ...value,
        description: value.description.trim() || null,
        systemPrompt: ai ? value.systemPrompt.trim() : null,
        modelRoutesJson: ai ? value.modelRoutesJson.trim() : null,
        toolPolicyJson: ai ? value.toolPolicyJson.trim() : null,
        maxInputTokens: ai ? value.maxInputTokens : null,
        maxOutputTokens: ai ? value.maxOutputTokens : null,
        maxEstimatedCostUsd: ai ? value.maxEstimatedCostUsd : null,
        maxAttempts: ai ? value.maxAttempts : 1,
      };
      if (this.agent)
        await firstValueFrom(
          this.api.update(this.agent.id, { ...content, version: this.agent.version }),
        );
      else await firstValueFrom(this.api.create(content));
      await this.router.navigate(['/agents'], { queryParams: { saved: 'true' } });
    } catch (error) {
      const message = agentErrorMessage(error);
      this.error.set(message);
      if (
        error instanceof HttpErrorResponse &&
        (error.error as { field?: string } | undefined)?.field === 'modelRoutesJson'
      )
        this.routeError.set(message);
    } finally {
      this.saving.set(false);
    }
  }

  routes(): ModelRouteDraft[] {
    try {
      const value = JSON.parse(this.form.controls.modelRoutesJson.value) as unknown;
      return Array.isArray(value) ? (value as ModelRouteDraft[]) : [];
    } catch {
      return [];
    }
  }

  async connectionChanged(): Promise<void> {
    const id = this.routeForm.controls.connectionId.value;
    if (!id) {
      this.routeModels.set([]);
      this.routeError.set('Не вибрано підключення для маршруту.');
      return;
    }
    const connection = this.providerConnections().find((item) => item.id === id);
    if (!connection || !this.connectionEligible(connection)) {
      this.routeModels.set([]);
      this.routeError.set(this.connectionUnavailableReason(connection));
      return;
    }
    this.routeError.set('');
    const paid = connection.provider === 'OpenAi' || connection.provider === 'XAi';
    if (!paid) {
      this.routeForm.controls.paidExecutionAllowed.setValue(false);
      this.routeForm.controls.paidFallbackAllowed.setValue(false);
    }
    try {
      this.routeModels.set(await firstValueFrom(this.providersApi.models(id)));
    } catch (error) {
      this.routeModels.set([]);
      this.error.set(directionErrorMessage(error));
    }
  }

  addRoute(): void {
    this.routeForm.markAllAsTouched();
    if (this.routeForm.invalid) {
      this.routeError.set('Перевірте підключення, модель, кількість спроб і timeout.');
      return;
    }
    const draft = this.routeForm.getRawValue();
    const connection = this.providerConnections().find((item) => item.id === draft.connectionId);
    if (!connection || !this.connectionEligible(connection)) {
      this.routeError.set(this.connectionUnavailableReason(connection));
      return;
    }
    const paid = connection.provider === 'OpenAi' || connection.provider === 'XAi';
    const routes = this.routes();
    routes.push({
      connectionId: connection.id,
      connectionName: connection.displayName,
      provider: this.logicalProvider(connection),
      modelId: draft.modelId,
      priority: routes.length + 1,
      enabled: true,
      allowedFallbackReasons: [
        'QuotaExceeded',
        'RateLimited',
        'TemporarilyUnavailable',
        'ModelUnavailable',
        'InsufficientFunds',
      ],
      maxAttempts: draft.maxAttempts,
      timeoutSeconds: draft.timeoutSeconds,
      paidExecutionAllowed: paid && draft.paidExecutionAllowed,
      paidFallbackAllowed: paid && draft.paidFallbackAllowed,
      ...(paid
        ? {
            safetyCapUsd: draft.safetyCapUsd,
            dailyCostCapUsd: draft.dailyCostCapUsd,
            monthlyCostCapUsd: draft.monthlyCostCapUsd,
            maxRequestsPerDay: draft.maxRequestsPerDay,
            maxRequestsPerMonth: draft.maxRequestsPerMonth,
          }
        : {}),
    });
    this.routeError.set('');
    this.setRoutes(routes);
  }

  removeRoute(index: number): void {
    const routes = this.routes();
    routes.splice(index, 1);
    this.setRoutes(routes);
  }
  moveRoute(index: number, offset: number): void {
    const routes = this.routes();
    const target = index + offset;
    if (target < 0 || target >= routes.length) return;
    [routes[index], routes[target]] = [routes[target], routes[index]];
    this.setRoutes(routes);
  }
  applyPreset(preset: RoutingPreset): void {
    const providers =
      preset === 'GitHubOnly'
        ? ['GitHubCopilot']
        : preset === 'LocalCopilotCli'
          ? ['GitHubCopilotLocalCli']
          : preset === 'OpenAIOnly'
            ? ['OpenAi']
            : preset === 'GrokOnly'
              ? ['XAi']
              : preset === 'GitHubThenOpenAI'
                ? ['GitHubCopilot', 'OpenAi']
                : ['GitHubCopilot', 'XAi'];
    const routes = providers
      .map((provider, index) => {
        const connection = this.providerConnections().find(
          (item) => item.provider === provider && this.connectionEligible(item),
        );
        if (!connection) return null;
        const paid = provider === 'OpenAi' || provider === 'XAi';
        return {
          connectionId: connection.id,
          connectionName: connection.displayName,
          provider: this.logicalProvider(connection),
          modelId: 'Auto',
          priority: index + 1,
          enabled: true,
          allowedFallbackReasons:
            provider === 'GitHubCopilotLocalCli'
              ? []
              : [
                  'QuotaExceeded',
                  'RateLimited',
                  'TemporarilyUnavailable',
                  'ModelUnavailable',
                  'InsufficientFunds',
                ],
          maxAttempts: 1,
          timeoutSeconds: 60,
          paidExecutionAllowed: paid,
          paidFallbackAllowed: paid && index > 0,
          ...(paid
            ? {
                safetyCapUsd: this.form.controls.maxEstimatedCostUsd.value,
                dailyCostCapUsd: this.form.controls.maxEstimatedCostUsd.value * 10,
                monthlyCostCapUsd: this.form.controls.maxEstimatedCostUsd.value * 100,
                maxRequestsPerDay: 20,
                maxRequestsPerMonth: 200,
              }
            : {}),
        } as ModelRouteDraft;
      })
      .filter((route): route is ModelRouteDraft => route !== null);
    if (routes.length !== providers.length) {
      this.routeError.set(this.presetUnavailableReason(preset));
      return;
    }
    this.setRoutes(routes);
    this.routeError.set('');
    if (routes.length === 1) {
      this.routeForm.controls.connectionId.setValue(routes[0].connectionId);
      this.routeForm.controls.modelId.setValue(routes[0].modelId);
      void this.connectionChanged();
    }
  }

  presetAvailable(preset: RoutingPreset): boolean {
    const required = this.presetProviders(preset);
    return required.every((provider) =>
      this.providerConnections().some(
        (connection) => connection.provider === provider && this.connectionEligible(connection),
      ),
    );
  }

  presetUnavailableReason(preset: RoutingPreset): string {
    const unavailable = this.presetProviders(preset).find(
      (provider) =>
        !this.providerConnections().some(
          (connection) => connection.provider === provider && this.connectionEligible(connection),
        ),
    );
    if (!unavailable) return '';
    if (unavailable === 'GitHubCopilotLocalCli') {
      const local = this.providerConnections().find(
        (connection) => connection.provider === 'GitHubCopilotLocalCli',
      );
      return this.connectionUnavailableReason(local);
    }
    return `Немає активного ${this.providerLabel(unavailable)} підключення.`;
  }

  connectionAccount(connection: ProviderConnectionDto): string {
    return connection.externalAccountLogin
      ? `@${connection.externalAccountLogin}`
      : 'акаунт не визначено';
  }

  connectionUnavailableReason(connection?: ProviderConnectionDto): string {
    if (!connection) return 'Підключення не знайдено для поточного користувача.';
    if (connection.status !== 'Active')
      return connection.lastSafeError ?? `Підключення має статус ${connection.status}.`;
    if (connection.provider === 'GitHubCopilotLocalCli') {
      if (!this.providerCapabilities()?.localCopilotCliEnabled)
        return 'Local Copilot CLI вимкнено конфігурацією сервера.';
      const status = this.localCliStatus();
      if (!status?.availableForCurrentUser)
        return 'Local Copilot CLI недоступний для цього користувача.';
      if (status.sidecarState !== 'Ready') return 'Local Copilot CLI sidecar недоступний.';
      if (status.authenticationState !== 'Active')
        return 'Local Copilot CLI потребує повторної авторизації.';
      if (!connection.lastSyncedAt)
        return 'Каталог моделей Local Copilot CLI ще не синхронізовано.';
    }
    return '';
  }

  private setRoutes(routes: ModelRouteDraft[]): void {
    this.form.controls.modelRoutesJson.setValue(
      JSON.stringify(routes.map((route, index) => ({ ...route, priority: index + 1 }))),
    );
    this.form.controls.modelRoutesJson.markAsDirty();
  }

  private async initialize(): Promise<void> {
    try {
      const page = await firstValueFrom(
        this.directionsApi.list({
          includeArchived: false,
          status: 'Active',
          page: 1,
          pageSize: 100,
        }),
      );
      this.directions.set(page.items);
      const [connections, capabilities] = await Promise.all([
        firstValueFrom(this.providersApi.list()),
        firstValueFrom(this.providersApi.capabilities()),
      ]);
      this.providerConnections.set(connections);
      this.providerCapabilities.set(capabilities);
      if (capabilities.localCopilotCliEnabled) {
        this.localCliStatus.set(await firstValueFrom(this.providersApi.localCopilotCliStatus()));
      }
      const eligible = this.activeConnections();
      if (eligible.length === 1) {
        this.routeForm.controls.connectionId.setValue(eligible[0].id);
        await this.connectionChanged();
      }
      const id = this.route.snapshot.paramMap.get('id');
      if (id) {
        this.isEdit.set(true);
        this.agent = await firstValueFrom(this.api.get(id));
        if (this.agent.isArchived)
          throw new Error('Архівованого агента потрібно спочатку відновити.');
        this.form.setValue({
          directionId: this.agent.directionId,
          name: this.agent.name,
          code: this.agent.code,
          description: this.agent.description ?? '',
          status: this.agent.status,
          executionType: this.agent.executionType,
          systemPrompt: this.agent.systemPrompt ?? '',
          modelRoutesJson: this.agent.modelRoutesJson ?? '[]',
          toolPolicyJson: this.agent.toolPolicyJson ?? '[]',
          maxInputTokens: this.agent.maxInputTokens ?? 8000,
          maxOutputTokens: this.agent.maxOutputTokens ?? 2000,
          maxEstimatedCostUsd: this.agent.maxEstimatedCostUsd ?? 1,
          maxAttempts: this.agent.maxAttempts ?? 3,
        });
      }
    } catch (error) {
      this.error.set(
        error instanceof Error && error.message ? error.message : directionErrorMessage(error),
      );
      this.form.disable();
    } finally {
      this.loading.set(false);
    }
  }

  private connectionEligible(connection: ProviderConnectionDto): boolean {
    return connection.status === 'Active' && !this.connectionUnavailableReason(connection);
  }

  private logicalProvider(connection: ProviderConnectionDto): string {
    return connection.provider === 'GitHubCopilotLocalCli'
      ? 'LocalCopilotCli'
      : connection.provider;
  }

  private presetProviders(preset: RoutingPreset): ProviderConnectionDto['provider'][] {
    switch (preset) {
      case 'GitHubOnly':
        return ['GitHubCopilot'];
      case 'LocalCopilotCli':
        return ['GitHubCopilotLocalCli'];
      case 'OpenAIOnly':
        return ['OpenAi'];
      case 'GrokOnly':
        return ['XAi'];
      case 'GitHubThenOpenAI':
        return ['GitHubCopilot', 'OpenAi'];
      case 'GitHubThenGrok':
        return ['GitHubCopilot', 'XAi'];
    }
  }

  private providerLabel(provider: ProviderConnectionDto['provider']): string {
    return {
      GitHubCopilot: 'GitHub Copilot',
      GitHubCopilotLocalCli: 'Local Copilot CLI',
      OpenAi: 'OpenAI',
      XAi: 'Grok',
      DeterministicTest: 'Deterministic Test Provider',
    }[provider];
  }
}
