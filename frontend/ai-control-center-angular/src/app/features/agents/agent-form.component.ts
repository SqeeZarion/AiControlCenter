import { Component, inject, OnInit, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { DirectionApiService } from '../directions/direction-api.service';
import { directionErrorMessage } from '../directions/direction-error';
import { DirectionDto } from '../directions/direction.models';
import { AgentApiService } from './agent-api.service';
import { AgentDefinitionDto, AgentStatus } from './agent.models';

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
  private agent?: AgentDefinitionDto;
  readonly directions = signal<DirectionDto[]>([]);
  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly error = signal('');
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
    executionType: ['Test' as const, Validators.required],
  });

  ngOnInit(): void {
    void this.initialize();
  }

  async submit(): Promise<void> {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.saving()) return;
    this.saving.set(true);
    this.error.set('');
    try {
      const value = this.form.getRawValue();
      const content = { ...value, description: value.description.trim() || null };
      if (this.agent)
        await firstValueFrom(
          this.api.update(this.agent.id, { ...content, version: this.agent.version }),
        );
      else await firstValueFrom(this.api.create(content));
      await this.router.navigate(['/agents'], { queryParams: { saved: 'true' } });
    } catch (error) {
      this.error.set(directionErrorMessage(error));
    } finally {
      this.saving.set(false);
    }
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
          executionType: 'Test',
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
}
