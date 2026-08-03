import { Component, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { GatewayApiService, ServiceInfo } from '../core/api/gateway-api.service';
import { RealtimeService } from '../core/realtime/realtime.service';
import { ConnectionStatusComponent } from '../shared/connection-status.component';

@Component({
  selector: 'app-shell',
  imports: [ConnectionStatusComponent],
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.scss',
})
export class ShellComponent implements OnInit, OnDestroy {
  private readonly gatewayApi = inject(GatewayApiService);
  readonly realtime = inject(RealtimeService);

  readonly gateway = signal<ServiceInfo | null>(null);
  readonly checked = signal(false);

  async ngOnInit(): Promise<void> {
    try {
      this.gateway.set(await firstValueFrom(this.gatewayApi.getServiceInfo()));
    } catch {
      this.gateway.set(null);
    }

    await this.realtime.connect();
    this.checked.set(true);
  }

  ngOnDestroy(): void {
    void this.realtime.disconnect();
  }
}
