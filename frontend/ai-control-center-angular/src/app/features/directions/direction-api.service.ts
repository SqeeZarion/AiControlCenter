import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import {
  CreateDirectionRequest,
  DirectionDto,
  DirectionFilters,
  DirectionListDto,
  DirectionStatus,
  UpdateDirectionRequest,
} from './direction.models';

@Injectable({ providedIn: 'root' })
export class DirectionApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/control-plane/v1/directions';

  list(filters: DirectionFilters) {
    let params = new HttpParams().set('includeArchived', filters.includeArchived);
    params = params.set('page', filters.page).set('pageSize', filters.pageSize);
    if (filters.status) params = params.set('status', filters.status);
    if (filters.search?.trim()) params = params.set('search', filters.search.trim());
    return this.http.get<DirectionListDto>(this.baseUrl, { params });
  }

  get(id: string) {
    return this.http.get<DirectionDto>(`${this.baseUrl}/${encodeURIComponent(id)}`);
  }
  create(request: CreateDirectionRequest) {
    return this.http.post<DirectionDto>(this.baseUrl, request);
  }
  update(id: string, request: UpdateDirectionRequest) {
    return this.http.put<DirectionDto>(`${this.baseUrl}/${encodeURIComponent(id)}`, request);
  }
  changeStatus(id: string, status: DirectionStatus, version: number) {
    return this.http.patch<DirectionDto>(`${this.baseUrl}/${encodeURIComponent(id)}/status`, {
      status,
      version,
    });
  }
  changeSortOrder(id: string, sortOrder: number, version: number) {
    return this.http.patch<DirectionDto>(`${this.baseUrl}/${encodeURIComponent(id)}/sort-order`, {
      sortOrder,
      version,
    });
  }
  archive(id: string, version: number) {
    return this.http.post<DirectionDto>(`${this.baseUrl}/${encodeURIComponent(id)}/archive`, {
      version,
    });
  }
  restore(id: string, version: number) {
    return this.http.post<DirectionDto>(`${this.baseUrl}/${encodeURIComponent(id)}/restore`, {
      version,
    });
  }
}
