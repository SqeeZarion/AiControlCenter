import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { of, throwError } from 'rxjs';
import { DirectionApiService } from './direction-api.service';
import { DirectionFormComponent } from './direction-form.component';
import { DirectionDto } from './direction.models';

describe('DirectionFormComponent', () => {
  let api: {
    create: ReturnType<typeof vi.fn>;
    get: ReturnType<typeof vi.fn>;
    update: ReturnType<typeof vi.fn>;
    changeStatus: ReturnType<typeof vi.fn>;
    changeSortOrder: ReturnType<typeof vi.fn>;
  };

  beforeEach(async () => {
    api = {
      create: vi.fn(() => of(createDirection())),
      get: vi.fn(),
      update: vi.fn(),
      changeStatus: vi.fn(),
      changeSortOrder: vi.fn(),
    };
    await TestBed.configureTestingModule({
      imports: [DirectionFormComponent],
      providers: [provideRouter([]), { provide: DirectionApiService, useValue: api }],
    }).compileComponents();
  });

  it('validates normalized name using Unicode scalar boundaries shared with the backend', () => {
    const fixture = TestBed.createComponent(DirectionFormComponent);
    fixture.detectChanges();

    fixture.componentInstance.form.patchValue({ name: '  x  ', code: 'bad/code' });
    expect(fixture.componentInstance.form.controls.name.invalid).toBe(true);
    expect(fixture.componentInstance.form.controls.code.invalid).toBe(true);

    fixture.componentInstance.form.patchValue({ name: '  AI   Notes  ', code: ' AI___Notes ' });
    expect(fixture.componentInstance.form.controls.name.valid).toBe(true);
    expect(fixture.componentInstance.form.controls.code.valid).toBe(true);

    fixture.componentInstance.form.patchValue({ name: '😀' });
    expect(fixture.componentInstance.form.controls.name.invalid).toBe(true);
    fixture.componentInstance.form.patchValue({ name: '😀'.repeat(100) });
    expect(fixture.componentInstance.form.controls.name.valid).toBe(true);
    fixture.componentInstance.form.patchValue({ name: '😀'.repeat(101) });
    expect(fixture.componentInstance.form.controls.name.invalid).toBe(true);
    fixture.componentInstance.form.patchValue({ name: 'e\u0301' });
    expect(fixture.componentInstance.form.controls.name.valid).toBe(true);

    for (const whiteSpace of ['\u0085', '\u00A0', '\u2007', '\u202F']) {
      fixture.componentInstance.form.patchValue({ name: `${whiteSpace}A` });
      expect(fixture.componentInstance.form.controls.name.invalid).toBe(true);
      fixture.componentInstance.form.patchValue({ name: `A${whiteSpace}${whiteSpace}B` });
      expect(fixture.componentInstance.form.controls.name.valid).toBe(true);
    }

    fixture.componentInstance.form.patchValue({ name: '\uFEFFA' });
    expect(fixture.componentInstance.form.controls.name.valid).toBe(true);
  });

  it('creates a direction and navigates back with a success marker', async () => {
    const fixture = TestBed.createComponent(DirectionFormComponent);
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    fixture.detectChanges();
    fixture.componentInstance.form.setValue({
      name: 'AI Notes',
      code: 'ai-notes',
      description: 'Description',
      icon: 'spark',
      status: 'Active',
      sortOrder: 10,
    });

    await fixture.componentInstance.submit();

    expect(api.create).toHaveBeenCalledWith({
      name: 'AI Notes',
      code: 'ai-notes',
      description: 'Description',
      icon: 'spark',
      status: 'Active',
      sortOrder: 10,
    });
    expect(navigate).toHaveBeenCalledWith(['/directions'], { queryParams: { saved: 'true' } });
  });

  it('shows backend validation and concurrency messages', async () => {
    api.create.mockReturnValue(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 400,
            error: { errors: { Name: ['Name is invalid.'], Code: ['Code is invalid.'] } },
          }),
      ),
    );
    const fixture = TestBed.createComponent(DirectionFormComponent);
    fixture.detectChanges();
    fixture.componentInstance.form.patchValue({ name: 'Valid name', code: 'valid-code' });
    await fixture.componentInstance.submit();
    expect(fixture.componentInstance.error()).toContain('Name is invalid. Code is invalid.');

    api.create.mockReturnValue(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 409,
            error: { title: 'Direction was changed. Reload and retry.' },
          }),
      ),
    );
    await fixture.componentInstance.submit();
    expect(fixture.componentInstance.error()).toBe('Direction was changed. Reload and retry.');
  });

  it('updates all editable fields with one atomic mutation request', async () => {
    const original = createDirection({ version: 7, status: 'Active', sortOrder: 1 });
    api.update.mockReturnValue(
      of(createDirection({ version: 8, name: 'Updated', status: 'Inactive', sortOrder: 5 })),
    );
    const fixture = TestBed.createComponent(DirectionFormComponent);
    const router = TestBed.inject(Router);
    vi.spyOn(router, 'navigate').mockResolvedValue(true);
    fixture.detectChanges();
    (fixture.componentInstance as unknown as { direction: DirectionDto }).direction = original;
    fixture.componentInstance.form.setValue({
      name: 'Updated',
      code: 'updated',
      description: '',
      icon: '',
      status: 'Inactive',
      sortOrder: 5,
    });

    await fixture.componentInstance.submit();

    expect(api.update).toHaveBeenCalledWith(original.id, {
      name: 'Updated',
      code: 'updated',
      description: null,
      icon: null,
      status: 'Inactive',
      sortOrder: 5,
      version: 7,
    });
    expect(api.changeStatus).not.toHaveBeenCalled();
    expect(api.changeSortOrder).not.toHaveBeenCalled();
  });

  it('does not navigate or start follow-up mutations when the atomic update fails', async () => {
    const original = createDirection({ version: 7 });
    api.update.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 409, error: { title: 'Conflict' } })),
    );
    const fixture = TestBed.createComponent(DirectionFormComponent);
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    fixture.detectChanges();
    (fixture.componentInstance as unknown as { direction: DirectionDto }).direction = original;
    fixture.componentInstance.form.setValue({
      name: 'Updated',
      code: 'updated',
      description: '',
      icon: '',
      status: 'Inactive',
      sortOrder: 5,
    });

    await fixture.componentInstance.submit();

    expect(api.update).toHaveBeenCalledTimes(1);
    expect(api.changeStatus).not.toHaveBeenCalled();
    expect(api.changeSortOrder).not.toHaveBeenCalled();
    expect(navigate).not.toHaveBeenCalled();
    expect(fixture.componentInstance.error()).toBe('Conflict');
  });
});

function createDirection(overrides: Partial<DirectionDto> = {}): DirectionDto {
  return {
    id: '11111111-1111-4111-8111-111111111111',
    name: 'Ideas',
    code: 'ideas',
    description: 'Description',
    icon: 'spark',
    status: 'Active',
    sortOrder: 1,
    createdAt: '2026-09-06T10:00:00Z',
    updatedAt: '2026-09-06T10:00:00Z',
    archivedAt: null,
    version: 7,
    isArchived: false,
    ...overrides,
  };
}
