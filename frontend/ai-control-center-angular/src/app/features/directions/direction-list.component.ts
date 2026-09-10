import { DatePipe } from '@angular/common';
import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthStore } from '../../core/auth/auth.store';
import { DirectionApiService } from './direction-api.service';
import { directionErrorMessage } from './direction-error';
import { DirectionDto, DirectionStatus } from './direction.models';

@Component({
  selector: 'app-direction-list',
  imports: [DatePipe, FormsModule, RouterLink],
  templateUrl: './direction-list.component.html',
  styleUrl: './directions.scss',
})
export class DirectionListComponent implements OnInit {
  private readonly api = inject(DirectionApiService);
  private readonly auth = inject(AuthStore);
  private readonly route = inject(ActivatedRoute);
  readonly items = signal<DirectionDto[]>([]);
  readonly loading = signal(true);
  readonly error = signal('');
  readonly success = signal('');
  readonly includeArchived = signal(false);
  readonly status = signal<DirectionStatus | ''>('');
  readonly search = signal('');
  readonly page = signal(1);
  readonly pageSize = 20;
  readonly totalCount = signal(0);
  readonly totalPages = signal(0);
  readonly canManage = computed(() => this.auth.hasAnyRole(['Admin']));

  ngOnInit(): void {
    if (this.route.snapshot.queryParamMap.get('saved') === 'true') {
      this.success.set('Напрямок успішно збережено.');
    }
    void this.load();
  }
  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set('');
    try {
      const response = await firstValueFrom(
        this.api.list({
          includeArchived: this.includeArchived(),
          status: this.status() || undefined,
          search: this.search(),
          page: this.page(),
          pageSize: this.pageSize,
        }),
      );
      if (
        response.items.length === 0 &&
        response.totalPages > 0 &&
        response.page > response.totalPages
      ) {
        this.page.set(response.totalPages);
        await this.load();
        return;
      }
      this.items.set(response.items);
      this.page.set(response.page);
      this.totalCount.set(response.totalCount);
      this.totalPages.set(response.totalPages);
    } catch (error) {
      this.items.set([]);
      this.totalCount.set(0);
      this.totalPages.set(0);
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
  async toggleStatus(direction: DirectionDto): Promise<void> {
    await this.runAction(
      () =>
        firstValueFrom(
          this.api.changeStatus(
            direction.id,
            direction.status === 'Active' ? 'Inactive' : 'Active',
            direction.version,
          ),
        ),
      'Статус напрямку оновлено.',
    );
  }
  async move(direction: DirectionDto, delta: number): Promise<void> {
    const nextOrder = Math.max(0, Math.min(100_000, direction.sortOrder + delta));
    if (nextOrder !== direction.sortOrder)
      await this.runAction(
        () => firstValueFrom(this.api.changeSortOrder(direction.id, nextOrder, direction.version)),
        'Порядок напрямку оновлено.',
      );
  }
  async archive(direction: DirectionDto): Promise<void> {
    if (confirm(`Архівувати «${direction.name}»?`))
      await this.runAction(
        () => firstValueFrom(this.api.archive(direction.id, direction.version)),
        'Напрямок архівовано.',
      );
  }
  async restore(direction: DirectionDto): Promise<void> {
    await this.runAction(
      () => firstValueFrom(this.api.restore(direction.id, direction.version)),
      'Напрямок відновлено.',
    );
  }
  private async runAction(action: () => Promise<DirectionDto>, message: string): Promise<void> {
    this.error.set('');
    this.success.set('');
    try {
      await action();
      this.success.set(message);
      await this.load();
    } catch (error) {
      this.error.set(directionErrorMessage(error));
    }
  }
}
