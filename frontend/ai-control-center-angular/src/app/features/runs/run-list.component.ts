import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { directionErrorMessage } from '../directions/direction-error';
import { RunApiService } from './run-api.service';
import { AgentRunDto, RunStatus } from './run.models';
import { formatUkrainianDate, runStatusLabel } from './run-presenter';

@Component({
  selector: 'app-run-list',
  imports: [FormsModule, RouterLink],
  templateUrl: './run-list.component.html',
  styleUrl: '../stage4.scss',
})
export class RunListComponent implements OnInit {
  private readonly api = inject(RunApiService);
  readonly items = signal<AgentRunDto[]>([]);
  readonly loading = signal(true);
  readonly error = signal('');
  readonly status = signal<RunStatus | ''>('');
  readonly agentId = signal('');
  readonly page = signal(1);
  readonly totalCount = signal(0);
  readonly totalPages = signal(0);
  readonly pageSize = 20;
  readonly statusLabel = runStatusLabel;
  readonly formatDate = formatUkrainianDate;

  ngOnInit(): void {
    void this.load();
  }
  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set('');
    try {
      const response = await firstValueFrom(
        this.api.list({
          status: this.status() || undefined,
          agentId: this.agentId().trim() || undefined,
          page: this.page(),
          pageSize: this.pageSize,
        }),
      );
      this.items.set(response.items);
      this.page.set(response.page);
      this.totalCount.set(response.totalCount);
      this.totalPages.set(response.totalPages);
    } catch (error) {
      this.items.set([]);
      this.error.set(directionErrorMessage(error));
    } finally {
      this.loading.set(false);
    }
  }
  async applyFilters(): Promise<void> {
    this.page.set(1);
    await this.load();
  }
  async changePage(page: number): Promise<void> {
    if (page < 1 || page > this.totalPages() || page === this.page()) return;
    this.page.set(page);
    await this.load();
  }
}
