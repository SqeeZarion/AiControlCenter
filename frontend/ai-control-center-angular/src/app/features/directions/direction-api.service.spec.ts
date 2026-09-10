import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { DirectionApiService } from './direction-api.service';
import { DirectionDto, DirectionListDto } from './direction.models';

describe('DirectionApiService', () => {
  const direction = createDirection();
  let api: DirectionApiService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(DirectionApiService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('maps list filters to the Gateway route', async () => {
    const response = firstValueFrom(
      api.list({
        includeArchived: true,
        status: 'Inactive',
        search: '  ideas  ',
        page: 2,
        pageSize: 20,
      }),
    );
    const request = http.expectOne(
      (candidate) =>
        candidate.url === '/api/control-plane/v1/directions' &&
        candidate.params.get('includeArchived') === 'true' &&
        candidate.params.get('status') === 'Inactive' &&
        candidate.params.get('search') === 'ideas' &&
        candidate.params.get('page') === '2' &&
        candidate.params.get('pageSize') === '20',
    );
    expect(request.request.method).toBe('GET');
    request.flush(listDto([direction], 2, 20, 21));
    expect((await response).items).toEqual([direction]);
  });

  it('maps every mutation without bypassing the Gateway', async () => {
    const create = firstValueFrom(
      api.create({
        name: 'Ideas',
        code: 'ideas',
        description: null,
        icon: null,
        status: 'Active',
        sortOrder: 1,
      }),
    );
    const createRequest = http.expectOne('/api/control-plane/v1/directions');
    expect(createRequest.request.method).toBe('POST');
    createRequest.flush(direction);
    await create;

    const update = firstValueFrom(
      api.update(direction.id, {
        name: 'Ideas',
        code: 'ideas',
        description: null,
        icon: null,
        status: 'Active',
        sortOrder: 1,
        version: 7,
      }),
    );
    const updateRequest = http.expectOne(`/api/control-plane/v1/directions/${direction.id}`);
    expect(updateRequest.request.method).toBe('PUT');
    expect(updateRequest.request.body.version).toBe(7);
    updateRequest.flush(direction);
    await update;

    const status = firstValueFrom(api.changeStatus(direction.id, 'Inactive', 8));
    const statusRequest = http.expectOne(`/api/control-plane/v1/directions/${direction.id}/status`);
    expect(statusRequest.request.method).toBe('PATCH');
    expect(statusRequest.request.body).toEqual({ status: 'Inactive', version: 8 });
    statusRequest.flush(direction);
    await status;

    const order = firstValueFrom(api.changeSortOrder(direction.id, 9, 9));
    const orderRequest = http.expectOne(
      `/api/control-plane/v1/directions/${direction.id}/sort-order`,
    );
    expect(orderRequest.request.method).toBe('PATCH');
    expect(orderRequest.request.body).toEqual({ sortOrder: 9, version: 9 });
    orderRequest.flush(direction);
    await order;

    const archive = firstValueFrom(api.archive(direction.id, 10));
    const archiveRequest = http.expectOne(
      `/api/control-plane/v1/directions/${direction.id}/archive`,
    );
    expect(archiveRequest.request.method).toBe('POST');
    archiveRequest.flush(direction);
    await archive;

    const restore = firstValueFrom(api.restore(direction.id, 11));
    const restoreRequest = http.expectOne(
      `/api/control-plane/v1/directions/${direction.id}/restore`,
    );
    expect(restoreRequest.request.method).toBe('POST');
    restoreRequest.flush(direction);
    await restore;
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
