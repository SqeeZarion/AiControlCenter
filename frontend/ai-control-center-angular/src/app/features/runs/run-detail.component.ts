import { Component, effect, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { finalize, Subscription } from 'rxjs';
import { RealtimeService } from '../../core/realtime/realtime.service';
import { directionErrorMessage } from '../directions/direction-error';
import { RunApiService } from './run-api.service';
import { AgentRunDto } from './run.models';
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
  private destroyed = false;
  readonly run = signal<AgentRunDto | null>(null);
  readonly loading = signal(true);
  readonly error = signal('');
  readonly statusLabel = runStatusLabel;
  readonly formatDate = formatUkrainianDate;

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
        },
        error: (error) => {
          if (!this.destroyed && requestSequence === this.latestRequestSequence)
            this.error.set(directionErrorMessage(error));
        },
      });
    this.requests.add(request);
  }
}
