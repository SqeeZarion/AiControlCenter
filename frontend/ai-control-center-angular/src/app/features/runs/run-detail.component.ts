import { Component, effect, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { finalize, firstValueFrom, Subscription } from 'rxjs';
import { RealtimeService } from '../../core/realtime/realtime.service';
import { directionErrorMessage } from '../directions/direction-error';
import { RunApiService } from './run-api.service';
import { AgentRunDto, AiProviderAttemptDto } from './run.models';
import { formatUkrainianDate, runStatusLabel, shouldRefreshRun } from './run-presenter';

@Component({
  selector: 'app-run-detail',
  imports: [RouterLink],
  templateUrl: './run-detail.component.html',
  styleUrl: '../stage4.scss',
})
export class RunDetailComponent implements OnInit, OnDestroy {
  private readonly api = inject(RunApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly realtime = inject(RealtimeService);
  private readonly runId = this.route.snapshot.paramMap.get('id') ?? '';
  private readonly requests = new Subscription();
  private lastReconnectGeneration = this.realtime.reconnectGeneration();
  private highestRequestedRevision = 0;
  private highestAppliedRevision = 0;
  private latestRequestSequence = 0;
  private activeRequests = 0;
  private preflightRevision = 0;
  private destroyed = false;
  readonly run = signal<AgentRunDto | null>(null);
  readonly loading = signal(true);
  readonly error = signal('');
  readonly preflightChecking = signal(false);
  readonly resumeReady = signal(false);
  readonly resumeBlockCode = signal<string | null>(null);
  readonly resumePending = signal(false);
  readonly statusLabel = runStatusLabel;
  readonly formatDate = formatUkrainianDate;
  readonly generationDispatchedLabel = (value: string | null): string =>
    value === 'true' ? 'Так' : value === 'false' ? 'Ні' : 'Невідомо';
  readonly usageValue = (value: number | null, source: string): string =>
    source === 'Unavailable' || value === null ? 'Unavailable' : String(value);
  readonly costValue = (value: number | null, source: string): string =>
    source === 'Unavailable' || value === null ? 'Unavailable' : `${value} USD`;
  readonly attemptDiagnosticMessage = (attempt: AiProviderAttemptDto): string => {
    switch (attempt.errorCode) {
      case 'model_not_available':
        return 'Запитану модель неможливо використати.';
      case 'session_configuration_invalid':
        return 'Конфігурація сесії Local Copilot CLI некоректна.';
      case 'session_model_required':
        return 'Local Copilot CLI вимагає модель для створення сесії.';
      case 'session_model_invalid':
        return 'Local Copilot CLI відхилив вибрану модель.';
      case 'session_option_unsupported':
        return 'Local Copilot CLI не підтримує один із параметрів сесії.';
      case 'session_runtime_incompatible':
        return 'Версії Copilot SDK і Local Copilot CLI несумісні.';
      case 'session_connection_rejected':
        return 'Local Copilot CLI відхилив підключення до runtime.';
      case 'session_create_protocol_error':
        return 'Local Copilot CLI повернув протокольну помилку створення сесії.';
      case 'session_create_failed_unknown':
        return 'Не вдалося безпечно класифікувати помилку створення сесії.';
      case 'dispatch_contract_invalid':
        return 'Внутрішній контракт відправлення Local Copilot CLI некоректний; запит провайдеру не надсилався.';
      case 'dispatch_request_invalid':
        return 'Local Copilot CLI відхилив запит до його відправлення.';
      case 'generation_authentication_rejected':
        return 'Local Copilot CLI відхилив автентифікацію запиту.';
      case 'generation_model_rejected':
        return 'Local Copilot CLI відхилив модель до виконання generation.';
      case 'generation_rate_limited':
        return 'GitHub Copilot обмежив частоту запитів.';
      case 'generation_policy_rejected':
        return 'GitHub Copilot заблокував запит політикою.';
      case 'generation_configuration_rejected':
        return 'Local Copilot CLI відхилив конфігурацію generation-запиту.';
      case 'generation_rpc_rejected':
        return 'Local Copilot CLI повернув типізовану RPC-відмову.';
      case 'generation_outcome_ambiguous':
        return 'Результат provider request невідомий. Повторне виконання заблоковано для запобігання подвійному списанню.';
      default:
        return attempt.errorCode ? 'Провайдер повернув типізовану помилку.' : '';
    }
  };
  async resume(): Promise<void> {
    const item = this.run();
    if (!item || !this.resumeReady() || this.resumePending() || this.resumeBlockCode() !== null)
      return;
    this.resumePending.set(true);
    this.resumeReady.set(false);
    try {
      this.run.set(await firstValueFrom(this.api.resume(item.id, crypto.randomUUID())));
    } catch (error) {
      this.error.set(directionErrorMessage(error));
    } finally {
      this.resumePending.set(false);
    }
  }

  constructor() {
    effect(() => {
      const event = this.realtime.latestRunEvent();
      const generation = this.realtime.reconnectGeneration();
      if (
        shouldRefreshRun(this.runId, this.highestRequestedRevision, event) &&
        event!.revision > this.highestAppliedRevision
      ) {
        this.highestRequestedRevision = event!.revision;
        this.load(false);
      }
      if (generation > this.lastReconnectGeneration) {
        this.lastReconnectGeneration = generation;
        this.load(false);
      }
    });
  }

  ngOnInit(): void {
    this.load(true);
  }
  ngOnDestroy(): void {
    this.destroyed = true;
    this.requests.unsubscribe();
  }

  private load(showLoading: boolean): void {
    if (showLoading) this.loading.set(true);
    const requestSequence = ++this.latestRequestSequence;
    this.activeRequests++;
    const request = this.api
      .get(this.runId)
      .pipe(
        finalize(() => {
          this.activeRequests--;
          if (!this.destroyed && this.activeRequests === 0) this.loading.set(false);
        }),
      )
      .subscribe({
        next: (response) => {
          if (this.destroyed || response.revision < this.highestAppliedRevision) return;
          if (response.revision === this.highestAppliedRevision && this.run() !== null) return;
          this.highestAppliedRevision = response.revision;
          this.highestRequestedRevision = Math.max(
            this.highestRequestedRevision,
            response.revision,
          );
          this.error.set('');
          this.run.set(response);
          this.checkProviderPreflight(response);
        },
        error: (error) => {
          if (!this.destroyed && requestSequence === this.latestRequestSequence)
            this.error.set(directionErrorMessage(error));
        },
      });
    this.requests.add(request);
  }

  private checkProviderPreflight(item: AgentRunDto): void {
    const paused =
      item.status === 'PausedProviderUnavailable' || item.status === 'PausedInsufficientFunds';
    if (!paused) {
      this.resumeReady.set(false);
      this.resumeBlockCode.set(null);
      this.preflightChecking.set(false);
      return;
    }
    if (this.preflightRevision === item.revision) return;
    this.preflightRevision = item.revision;
    this.resumeReady.set(false);
    this.resumeBlockCode.set(null);
    this.preflightChecking.set(true);
    const revision = item.revision;
    const request = this.api
      .preflight(item.id)
      .pipe(finalize(() => this.preflightChecking.set(false)))
      .subscribe({
        next: (result) => {
          if (this.run()?.revision !== revision) return;
          this.resumeReady.set(result.available);
          this.resumeBlockCode.set(
            result.code === 'LegacyAmbiguousOutcome' || result.code === 'AmbiguousOutcome'
              ? result.code
              : null,
          );
          if (!result.available && this.resumeBlockCode() === null)
            this.error.set(
              'Провайдер ще недоступний. Перевірте підключення перед продовженням Run.',
            );
          else if (this.resumeBlockCode() !== null) this.error.set('');
        },
        error: () => {
          if (this.run()?.revision === revision)
            this.error.set('Не вдалося підтвердити готовність провайдера.');
        },
      });
    this.requests.add(request);
  }
}
