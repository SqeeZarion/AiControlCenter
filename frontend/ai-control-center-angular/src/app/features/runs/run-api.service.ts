import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { AgentRunDto, AgentRunListDto, RunFilters, TestRunOutcome } from './run.models';

@Injectable({ providedIn: 'root' })
export class RunApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/orchestrator/v1/runs';

  list(filters: RunFilters) {
    let params = new HttpParams().set('page', filters.page).set('pageSize', filters.pageSize);
    if (filters.status) params = params.set('status', filters.status);
    if (filters.agentId) params = params.set('agentId', filters.agentId);
    return this.http.get<AgentRunListDto>(this.baseUrl, { params });
  }

  get(id: string) {
    return this.http.get<AgentRunDto>(`${this.baseUrl}/${encodeURIComponent(id)}`);
  }

  create(agentId: string, input: string, expectedOutcome: TestRunOutcome) {
    return this.http.post<AgentRunDto>(this.baseUrl, { agentId, input, expectedOutcome });
  }
}
