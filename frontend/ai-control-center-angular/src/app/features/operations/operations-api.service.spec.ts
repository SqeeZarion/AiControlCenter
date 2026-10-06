import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { OperationsApiService } from './operations-api.service';

describe('OperationsApiService', () => {
  it('loads the protected Gateway snapshot endpoint', () => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const api = TestBed.inject(OperationsApiService);
    const http = TestBed.inject(HttpTestingController);

    api.getSnapshot().subscribe((snapshot) => expect(snapshot.freshnessSeconds).toBe(30));
    http.expectOne('/api/gateway/v1/operations/snapshot').flush({
      generatedAt: '2026-09-30T12:00:00Z',
      freshnessSeconds: 30,
      nodes: [],
      queues: [],
      workload: { recentRuns: [] },
    });
    http.verify();
  });
});
