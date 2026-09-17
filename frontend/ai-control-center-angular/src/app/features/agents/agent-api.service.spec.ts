import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { AgentApiService } from './agent-api.service';
import { AgentDefinitionDto } from './agent.models';

describe('AgentApiService', () => {
  let api: AgentApiService;
  let http: HttpTestingController;
  const agent: AgentDefinitionDto = {
    id: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
    directionId: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
    name: 'Test Agent',
    code: 'test-agent',
    description: null,
    status: 'Active',
    executionType: 'Test',
    createdAt: '2026-09-12T12:00:00Z',
    updatedAt: '2026-09-12T12:00:00Z',
    archivedAt: null,
    version: 2,
    isArchived: false,
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(AgentApiService);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  it('maps filters and mutations only through Gateway routes', async () => {
    const list = firstValueFrom(
      api.list({
        includeArchived: true,
        status: 'Active',
        directionId: agent.directionId,
        search: ' test ',
        page: 2,
        pageSize: 20,
      }),
    );
    const listRequest = http.expectOne(
      (request) =>
        request.url === '/api/control-plane/v1/agents' &&
        request.params.get('search') === 'test' &&
        request.params.get('directionId') === agent.directionId &&
        request.params.get('page') === '2',
    );
    listRequest.flush({ items: [agent], page: 2, pageSize: 20, totalCount: 21, totalPages: 2 });
    await list;
    const create = firstValueFrom(
      api.create({
        directionId: agent.directionId,
        name: agent.name,
        code: agent.code,
        description: null,
        status: 'Active',
        executionType: 'Test',
      }),
    );
    const createRequest = http.expectOne('/api/control-plane/v1/agents');
    expect(createRequest.request.method).toBe('POST');
    createRequest.flush(agent);
    await create;
    const status = firstValueFrom(api.changeStatus(agent.id, 'Inactive', 2));
    const statusRequest = http.expectOne(`/api/control-plane/v1/agents/${agent.id}/status`);
    expect(statusRequest.request.body).toEqual({ status: 'Inactive', version: 2 });
    statusRequest.flush(agent);
    await status;
  });
});
