import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { OperationsCenterSnapshotDto } from './operations.models';

@Injectable({ providedIn: 'root' })
export class OperationsApiService {
  private readonly http = inject(HttpClient);

  getSnapshot() {
    return this.http.get<OperationsCenterSnapshotDto>('/api/gateway/v1/operations/snapshot');
  }
}
