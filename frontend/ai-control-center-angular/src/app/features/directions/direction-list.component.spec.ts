import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, Subject, throwError } from 'rxjs';
import { AuthStore } from '../../core/auth/auth.store';
import { DirectionApiService } from './direction-api.service';
import { DirectionListComponent } from './direction-list.component';
import { DirectionDto, DirectionListDto } from './direction.models';

describe('DirectionListComponent', () => {
  let fixture: ComponentFixture<DirectionListComponent>;
  let isAdmin: boolean;
  let api: {
    list: ReturnType<typeof vi.fn>;
    changeStatus: ReturnType<typeof vi.fn>;
    changeSortOrder: ReturnType<typeof vi.fn>;
    archive: ReturnType<typeof vi.fn>;
    restore: ReturnType<typeof vi.fn>;
  };

  beforeEach(async () => {
    isAdmin = false;
    api = {
      list: vi.fn(() => of(listDto([]))),
      changeStatus: vi.fn(),
      changeSortOrder: vi.fn(),
      archive: vi.fn(),
      restore: vi.fn(),
    };
    await TestBed.configureTestingModule({
      imports: [DirectionListComponent],
      providers: [
        provideRouter([]),
        { provide: DirectionApiService, useValue: api },
        { provide: AuthStore, useValue: { hasAnyRole: () => isAdmin } },
      ],
    }).compileComponents();
  });

  it('renders loading and empty states without management buttons for a non-admin', async () => {
    const pending = new Subject<DirectionListDto>();
    api.list.mockReturnValue(pending);
    fixture = TestBed.createComponent(DirectionListComponent);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Завантаження напрямків');

    pending.next(listDto([]));
    pending.complete();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Напрямків за вибраними фільтрами немає');
    expect(fixture.nativeElement.textContent).not.toContain('Додати напрямок');
  });

  it('renders data and management actions only for an admin', async () => {
    isAdmin = true;
    api.list.mockReturnValue(of(listDto([createDirection()])));
    fixture = TestBed.createComponent(DirectionListComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent;
    expect(text).toContain('Ideas');
    expect(text).toContain('Додати напрямок');
    expect(text).toContain('Редагувати');
    expect(text).toContain('Архівувати');
  });

  it('shows backend errors and maps status and sorting actions with the current version', async () => {
    isAdmin = true;
    const direction = createDirection();
    api.list
      .mockReturnValueOnce(of(listDto([direction])))
      .mockReturnValue(of(listDto([direction])));
    api.changeStatus.mockReturnValue(of({ ...direction, status: 'Inactive', version: 8 }));
    api.changeSortOrder.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 409, error: { title: 'Conflict' } })),
    );
    fixture = TestBed.createComponent(DirectionListComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    await fixture.componentInstance.toggleStatus(direction);
    expect(api.changeStatus).toHaveBeenCalledWith(direction.id, 'Inactive', direction.version);

    await fixture.componentInstance.move(direction, 1);
    expect(api.changeSortOrder).toHaveBeenCalledWith(direction.id, 2, direction.version);
    expect(fixture.componentInstance.error()).toBe('Conflict');
  });

  it('requires confirmation before archive and can restore an archived direction', async () => {
    isAdmin = true;
    const direction = createDirection();
    const archived = createDirection({
      archivedAt: '2026-09-06T11:00:00Z',
      isArchived: true,
      version: 8,
    });
    api.list.mockReturnValue(of(listDto([])));
    api.archive.mockReturnValue(of(archived));
    api.restore.mockReturnValue(of(createDirection({ version: 9 })));
    fixture = TestBed.createComponent(DirectionListComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    const confirm = vi.spyOn(globalThis, 'confirm').mockReturnValue(false);

    await fixture.componentInstance.archive(direction);
    expect(api.archive).not.toHaveBeenCalled();

    confirm.mockReturnValue(true);
    await fixture.componentInstance.archive(direction);
    expect(api.archive).toHaveBeenCalledWith(direction.id, direction.version);
    await fixture.componentInstance.restore(archived);
    expect(api.restore).toHaveBeenCalledWith(archived.id, archived.version);
  });

  it('renders a list error state', async () => {
    const existing = createDirection();
    api.list
      .mockReturnValueOnce(of(listDto([existing])))
      .mockReturnValue(throwError(() => new HttpErrorResponse({ status: 401 })));
    fixture = TestBed.createComponent(DirectionListComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(fixture.componentInstance.items()).toEqual([existing]);

    await fixture.componentInstance.load();
    fixture.detectChanges();

    expect(fixture.componentInstance.items()).toEqual([]);
    expect(fixture.nativeElement.textContent).not.toContain('Ideas');
  });

  it('moves back to the last available page after the current page becomes empty', async () => {
    const remaining = createDirection();
    api.list
      .mockReturnValueOnce(of(listDto([], 2, 20, 1)))
      .mockReturnValueOnce(of(listDto([remaining], 1, 20, 1)));
    fixture = TestBed.createComponent(DirectionListComponent);
    fixture.componentInstance.page.set(2);

    await fixture.componentInstance.load();

    expect(api.list).toHaveBeenCalledTimes(2);
    expect(fixture.componentInstance.page()).toBe(1);
    expect(fixture.componentInstance.items()).toEqual([remaining]);
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

function listDto(
  items: DirectionDto[],
  page = 1,
  pageSize = 20,
  totalCount = items.length,
): DirectionListDto {
  return {
    items,
    page,
    pageSize,
    totalCount,
    totalPages: totalCount === 0 ? 0 : Math.ceil(totalCount / pageSize),
  };
}
