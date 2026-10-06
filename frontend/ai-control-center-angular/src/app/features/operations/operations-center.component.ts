import { Component, computed, effect, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { RealtimeService } from '../../core/realtime/realtime.service';
import { RunApiService } from '../runs/run-api.service';
import { AgentRunDto } from '../runs/run.models';
import { formatUkrainianDate, runStatusLabel } from '../runs/run-presenter';
import { OperationsApiService } from './operations-api.service';
import { OperationsCenterSnapshotDto, OperationsNodeDto } from './operations.models';
import {
  deriveOperationsState,
  initialOperationsPollingState,
  isSnapshotStale,
  nextPollingDelayMilliseconds,
  pollingFailed,
  pollingSucceeded,
} from './operations-state';

@Component({
  selector: 'app-operations-center',
  imports: [RouterLink],
  templateUrl: './operations-center.component.html',
  styleUrl: './operations-center.component.scss',
})
export class OperationsCenterComponent implements OnInit, OnDestroy {
  private readonly api = inject(OperationsApiService);
  private readonly runsApi = inject(RunApiService);
  readonly realtime = inject(RealtimeService);
  private timer?: ReturnType<typeof setTimeout>;
  private clock?: ReturnType<typeof setInterval>;
  private started = false;
  private destroyed = false;
  private refreshInFlight = false;
  private refreshPending = false;
  private lastEventId = '';
  private lastReconnectGeneration = this.realtime.reconnectGeneration();

  readonly snapshot = signal<OperationsCenterSnapshotDto | null>(null);
  readonly polling = signal(initialOperationsPollingState);
  readonly now = signal(Date.now());
  readonly loading = signal(true);
  readonly error = signal('');
  readonly selectedNodeId = signal('gateway');
  readonly selectedRunId = signal<string | null>(null);
  readonly selectedRun = signal<AgentRunDto | null>(null);
  readonly runLoading = signal(false);
  readonly runError = signal('');
  readonly formatDate = formatUkrainianDate;
  readonly runStatusLabel = runStatusLabel;

  readonly state = computed(() =>
    deriveOperationsState(this.snapshot(), this.polling(), this.realtime.isConnected(), this.now()),
  );
  readonly stale = computed(() => isSnapshotStale(this.snapshot(), this.now()));
  readonly selectedNode = computed<OperationsNodeDto | null>(() => {
    const selected = this.selectedNodeId();
    return this.snapshot()?.nodes.find((node) => node.id === selected) ?? null;
  });

  constructor() {
    effect(() => {
      const event = this.realtime.latestRunEvent();
      const generation = this.realtime.reconnectGeneration();
      if (!this.started) return;

      if (event && event.eventId !== this.lastEventId) {
        this.lastEventId = event.eventId;
        this.requestImmediateRefresh();
        if (this.selectedRunId() === event.runId) void this.loadRun(event.runId);
      }
      if (generation > this.lastReconnectGeneration) {
        this.lastReconnectGeneration = generation;
        this.requestImmediateRefresh();
      }
    });
  }

  ngOnInit(): void {
    this.started = true;
    this.clock = setInterval(() => this.now.set(Date.now()), 1_000);
    void this.refresh();
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    if (this.timer) clearTimeout(this.timer);
    if (this.clock) clearInterval(this.clock);
  }

  refreshNow(): void {
    this.requestImmediateRefresh();
  }

  selectNode(id: string): void {
    this.selectedNodeId.set(id);
  }

  selectRun(id: string): void {
    if (this.selectedRunId() !== id) {
      this.selectedRun.set(null);
      this.runError.set('');
    }
    this.selectedRunId.set(id);
    void this.loadRun(id);
  }

  private requestImmediateRefresh(): void {
    if (this.destroyed) return;
    if (this.timer) clearTimeout(this.timer);
    if (this.refreshInFlight) {
      this.refreshPending = true;
      return;
    }
    void this.refresh();
  }

  private async refresh(): Promise<void> {
    if (this.destroyed || this.refreshInFlight) return;
    this.refreshInFlight = true;
    try {
      const response = await firstValueFrom(this.api.getSnapshot());
      if (this.destroyed) return;
      this.snapshot.set(response);
      this.polling.set(pollingSucceeded(Date.now()));
      this.error.set('');
      const selectedId = this.selectedRunId();
      if (selectedId && response.workload.recentRuns.some((run) => run.id === selectedId))
        await this.loadRun(selectedId);
    } catch {
      if (this.destroyed) return;
      this.polling.update(pollingFailed);
      this.error.set(
        'Не вдалося оновити live snapshot. Останні відомі дані залишаються на екрані.',
      );
    } finally {
      this.refreshInFlight = false;
      this.loading.set(false);
      if (!this.destroyed) {
        if (this.refreshPending) {
          this.refreshPending = false;
          void this.refresh();
        } else {
          this.timer = setTimeout(
            () => void this.refresh(),
            nextPollingDelayMilliseconds(this.polling().consecutiveFailures),
          );
        }
      }
    }
  }

  private async loadRun(id: string): Promise<void> {
    this.runLoading.set(true);
    try {
      const response = await firstValueFrom(this.runsApi.get(id));
      if (this.destroyed || this.selectedRunId() !== id || response.id !== id) return;
      const current = this.selectedRun();
      if (!current || current.id !== response.id || response.revision >= current.revision)
        this.selectedRun.set(response);
      this.runError.set('');
    } catch {
      if (!this.destroyed && this.selectedRunId() === id)
        this.runError.set('Деталі Run тимчасово недоступні.');
    } finally {
      if (!this.destroyed && this.selectedRunId() === id) this.runLoading.set(false);
    }
  }
}
