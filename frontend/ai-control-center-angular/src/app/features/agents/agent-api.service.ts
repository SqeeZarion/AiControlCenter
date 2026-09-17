import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import {
  AgentDefinitionDto,
  AgentDefinitionListDto,
  AgentFilters,
  AgentStatus,
  CreateAgentRequest,
  UpdateAgentRequest,
} from './agent.models';

@Injectable({ providedIn: 'root' })
export class AgentApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/control-plane/v1/agents';

  list(filters: AgentFilters) {
    let params = new HttpParams()
      .set('includeArchived', filters.includeArchived)
      .set('page', filters.page)
      .set('pageSize', filters.pageSize);
    if (filters.status) params = params.set('status', filters.status);
    if (filters.directionId) params = params.set('directionId', filters.directionId);
    if (filters.search?.trim()) params = params.set('search', filters.search.trim());
    return this.http.get<AgentDefinitionListDto>(this.baseUrl, { params });
  }

  get(id: string) {
    return this.http.get<AgentDefinitionDto>(`${this.baseUrl}/${encodeURIComponent(id)}`);
  }

  create(request: CreateAgentRequest) {
    return this.http.post<AgentDefinitionDto>(this.baseUrl, request);
  }

  update(id: string, request: UpdateAgentRequest) {
    return this.http.put<AgentDefinitionDto>(`${this.baseUrl}/${encodeURIComponent(id)}`, request);
  }

  changeStatus(id: string, status: AgentStatus, version: number) {
    return this.http.patch<AgentDefinitionDto>(`${this.baseUrl}/${encodeURIComponent(id)}/status`, {
      status,
      version,
    });
  }

  archive(id: string, version: number) {
    return this.http.post<AgentDefinitionDto>(`${this.baseUrl}/${encodeURIComponent(id)}/archive`, {
      version,
    });
  }

  restore(id: string, version: number) {
    return this.http.post<AgentDefinitionDto>(`${this.baseUrl}/${encodeURIComponent(id)}/restore`, {
      version,
    });
  }
}
