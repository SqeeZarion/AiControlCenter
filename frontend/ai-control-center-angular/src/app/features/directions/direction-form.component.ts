import { Component, inject, OnInit, signal } from '@angular/core';
import {
  AbstractControl,
  FormBuilder,
  ReactiveFormsModule,
  ValidationErrors,
  ValidatorFn,
  Validators,
} from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { DirectionApiService } from './direction-api.service';
import { directionErrorMessage } from './direction-error';
import { DirectionDto, DirectionStatus } from './direction.models';

@Component({
  selector: 'app-direction-form',
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './direction-form.component.html',
  styleUrl: './directions.scss',
})
export class DirectionFormComponent implements OnInit {
  private readonly api = inject(DirectionApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly fb = inject(FormBuilder);
  private direction?: DirectionDto;
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly error = signal('');
  readonly isEdit = signal(false);
  readonly form = this.fb.nonNullable.group({
    name: ['', [normalizedNameValidator()]],
    code: ['', [normalizedCodeValidator()]],
    description: ['', trimmedMaxLengthValidator(1000)],
    icon: ['', trimmedMaxLengthValidator(100)],
    status: ['Active' as DirectionStatus, Validators.required],
    sortOrder: [0, [Validators.required, Validators.min(0), Validators.max(100_000)]],
  });

  ngOnInit(): void {
    const id = this.route.snapshot.paramMap.get('id');
    if (id) void this.load(id);
  }

  async submit(): Promise<void> {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.saving()) return;
    this.saving.set(true);
    this.error.set('');
    try {
      const value = this.form.getRawValue();
      if (!this.direction) {
        await firstValueFrom(
          this.api.create({
            ...value,
            description: value.description.trim() || null,
            icon: value.icon.trim() || null,
          }),
        );
      } else {
        const updated = await firstValueFrom(
          this.api.update(this.direction.id, {
            name: value.name,
            code: value.code,
            description: value.description.trim() || null,
            icon: value.icon.trim() || null,
            status: value.status,
            sortOrder: value.sortOrder,
            version: this.direction.version,
          }),
        );
        this.direction = updated;
      }
      await this.router.navigate(['/directions'], { queryParams: { saved: 'true' } });
    } catch (error) {
      this.error.set(directionErrorMessage(error));
    } finally {
      this.saving.set(false);
    }
  }

  private async load(id: string): Promise<void> {
    this.isEdit.set(true);
    this.loading.set(true);
    try {
      this.direction = await firstValueFrom(this.api.get(id));
      if (this.direction.isArchived) {
        this.error.set('Архівований напрямок потрібно спочатку відновити зі списку.');
        this.form.disable();
        return;
      }
      this.form.setValue({
        name: this.direction.name,
        code: this.direction.code,
        description: this.direction.description ?? '',
        icon: this.direction.icon ?? '',
        status: this.direction.status,
        sortOrder: this.direction.sortOrder,
      });
    } catch (error) {
      this.error.set(directionErrorMessage(error));
      this.form.disable();
    } finally {
      this.loading.set(false);
    }
  }
}

function normalizedNameValidator(): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null => {
    const normalized = normalizeUnicodeWhiteSpace(String(control.value ?? ''));
    const scalarCount = Array.from(normalized).length;
    return scalarCount >= 2 && scalarCount <= 100 ? null : { normalizedName: true };
  };
}

function normalizeUnicodeWhiteSpace(value: string): string {
  return value
    .replace(/^\p{White_Space}+|\p{White_Space}+$/gu, '')
    .replace(/\p{White_Space}+/gu, ' ');
}

function normalizedCodeValidator(): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null => {
    const normalized = String(control.value ?? '')
      .trim()
      .toLowerCase()
      .replace(/[\s_]+/g, '-')
      .replace(/-+/g, '-')
      .replace(/^-|-$/g, '');
    return normalized.length >= 2 &&
      normalized.length <= 64 &&
      /^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(normalized)
      ? null
      : { normalizedCode: true };
  };
}

function trimmedMaxLengthValidator(maxLength: number): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null =>
    String(control.value ?? '').trim().length <= maxLength ? null : { maxlength: true };
}
