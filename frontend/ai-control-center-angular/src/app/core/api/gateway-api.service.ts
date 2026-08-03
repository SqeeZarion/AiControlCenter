import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { APP_ENVIRONMENT } from '../configuration/app-environment';

export interface ServiceInfo {
  service: string;
  version: string;
  environment: string;
}

@Injectable({ providedIn: 'root' })
export class GatewayApiService {
  private readonly http = inject(HttpClient);
  private readonly config = inject(APP_ENVIRONMENT);

  getServiceInfo() {
    return this.http.get<ServiceInfo>(`${this.config.gatewayBaseUrl}/api/gateway/service-info`);
  }
}
