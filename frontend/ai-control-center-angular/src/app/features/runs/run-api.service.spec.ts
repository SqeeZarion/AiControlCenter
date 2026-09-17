import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { RunApiService } from './run-api.service';

describe('RunApiService', () => {
  let api: RunApiService;
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(RunApiService);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  it('uses Orchestrator Gateway routes for create, list and details', async () => {
    const create = firstValueFrom(api.create('agent-id', 'input', 'Succeed'));
    const createRequest = http.expectOne('/api/orchestrator/v1/runs');
    expect(createRequest.request.method).toBe('POST');
    expect(createRequest.request.body).toEqual({
      agentId: 'agent-id',
      input: 'input',
      expectedOutcome: 'Succeed',
    });
    createRequest.flush({ id: 'run-id' });
    await create;
    const list = firstValueFrom(
      api.list({ status: 'Running', agentId: 'agent-id', page: 3, pageSize: 20 }),
    );
    const listRequest = http.expectOne(
      (request) =>
        request.url === '/api/orchestrator/v1/runs' &&
        request.params.get('status') === 'Running' &&
        request.params.get('page') === '3',
    );
    listRequest.flush({ items: [], page: 3, pageSize: 20, totalCount: 0, totalPages: 0 });
    await list;
    const details = firstValueFrom(api.get('run/id'));
    const detailsRequest = http.expectOne('/api/orchestrator/v1/runs/run%2Fid');
    expect(detailsRequest.request.method).toBe('GET');
    detailsRequest.flush({ id: 'run/id' });
    await details;
  });
});
