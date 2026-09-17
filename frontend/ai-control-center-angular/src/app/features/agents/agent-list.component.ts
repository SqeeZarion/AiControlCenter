import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthStore } from '../../core/auth/auth.store';
import { DirectionApiService } from '../directions/direction-api.service';
import { directionErrorMessage } from '../directions/direction-error';
import { DirectionDto } from '../directions/direction.models';
import { RunApiService } from '../runs/run-api.service';
import { TestRunOutcome } from '../runs/run.models';
import { AgentApiService } from './agent-api.service';
import { AgentDefinitionDto, AgentStatus } from './agent.models';

@Component({
  selector: 'app-agent-list',
  imports: [FormsModule, RouterLink],
  templateUrl: './agent-list.component.html',
  styleUrl: '../stage4.scss',
})
export class AgentListComponent implements OnInit {
  private readonly api = inject(AgentApiService);
  private readonly directionsApi = inject(DirectionApiService);
  private readonly runsApi = inject(RunApiService);
  private readonly auth = inject(AuthStore);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  readonly items = signal<AgentDefinitionDto[]>([]);
  readonly directions = signal<DirectionDto[]>([]);
  readonly loading = signal(true);
  readonly launchingId = signal('');
  readonly error = signal('');
  readonly success = signal('');
  readonly includeArchived = signal(false);
  readonly status = signal<AgentStatus | ''>('');
  readonly directionId = signal('');
  readonly search = signal('');
  readonly page = signal(1);
  readonly totalCount = signal(0);
  readonly totalPages = signal(0);
  readonly pageSize = 20;
  readonly canManage = computed(() => this.auth.hasAnyRole(['Admin', 'Developer']));
  readonly runInput = new Map<string, string>();
  readonly runOutcome = new Map<string, TestRunOutcome>();

  ngOnInit(): void {
    if (this.route.snapshot.queryParamMap.get('saved') === 'true') {
      this.success.set('Агента успішно збережено.');
    }
    void Promise.all([this.loadDirections(), this.load()]);
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set('');
    try {
      const response = await firstValueFrom(
        this.api.list({
          includeArchived: this.includeArchived(),
          status: this.status() || undefined,
          directionId: this.directionId() || undefined,
          search: this.search(),
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

  directionName(id: string): string {
    return this.directions().find((direction) => direction.id === id)?.name ?? id;
  }

  inputFor(id: string): string {
    return this.runInput.get(id) ?? '';
  }

  setInput(id: string, value: string): void {
    this.runInput.set(id, value);
  }

  outcomeFor(id: string): TestRunOutcome {
    return this.runOutcome.get(id) ?? 'Succeed';
  }

  setOutcome(id: string, value: string): void {
    this.runOutcome.set(id, value as TestRunOutcome);
  }

  async launch(agent: AgentDefinitionDto): Promise<void> {
    this.launchingId.set(agent.id);
    this.error.set('');
    try {
      const run = await firstValueFrom(
        this.runsApi.create(agent.id, this.inputFor(agent.id), this.outcomeFor(agent.id)),
      );
      await this.router.navigate(['/runs', run.id]);
    } catch (error) {
      this.error.set(directionErrorMessage(error));
    } finally {
      this.launchingId.set('');
    }
  }

  async toggleStatus(agent: AgentDefinitionDto): Promise<void> {
    await this.runAction(() =>
      firstValueFrom(
        this.api.changeStatus(
          agent.id,
          agent.status === 'Active' ? 'Inactive' : 'Active',
          agent.version,
        ),
      ),
    );
  }

  async archive(agent: AgentDefinitionDto): Promise<void> {
    if (confirm(`Архівувати «${agent.name}»?`)) {
      await this.runAction(() => firstValueFrom(this.api.archive(agent.id, agent.version)));
    }
  }

  async restore(agent: AgentDefinitionDto): Promise<void> {
    await this.runAction(() => firstValueFrom(this.api.restore(agent.id, agent.version)));
  }

  private async loadDirections(): Promise<void> {
    try {
      const response = await firstValueFrom(
        this.directionsApi.list({ includeArchived: false, page: 1, pageSize: 100 }),
      );
      this.directions.set(response.items);
    } catch {
      this.directions.set([]);
    }
  }

  private async runAction(action: () => Promise<AgentDefinitionDto>): Promise<void> {
    this.error.set('');
    try {
      await action();
      await this.load();
    } catch (error) {
      this.error.set(directionErrorMessage(error));
    }
  }
}
