import { computed, inject, Injectable, signal } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState } from '@microsoft/signalr';
import { APP_ENVIRONMENT } from '../configuration/app-environment';

interface TechnicalPong {
  service: string;
  timestamp: string;
}

@Injectable({ providedIn: 'root' })
export class RealtimeService {
  private readonly config = inject(APP_ENVIRONMENT);
  private readonly connectionState = signal<HubConnectionState>(HubConnectionState.Disconnected);
  private connection?: HubConnection;

  readonly state = this.connectionState.asReadonly();
  readonly isConnected = computed(() => this.connectionState() === HubConnectionState.Connected);

  async connect(): Promise<TechnicalPong | null> {
    if (this.connection?.state === HubConnectionState.Connected) {
      return this.connection.invoke<TechnicalPong>('Ping');
    }

    this.connectionState.set(HubConnectionState.Connecting);
    this.connection = new HubConnectionBuilder()
      .withUrl(`${this.config.gatewayBaseUrl}${this.config.systemHubPath}`)
      .withAutomaticReconnect()
      .build();

    this.connection.onreconnecting(() => this.connectionState.set(HubConnectionState.Reconnecting));
    this.connection.onreconnected(() => this.connectionState.set(HubConnectionState.Connected));
    this.connection.onclose(() => this.connectionState.set(HubConnectionState.Disconnected));

    try {
      await this.connection.start();
      this.connectionState.set(HubConnectionState.Connected);
      return await this.connection.invoke<TechnicalPong>('Ping');
    } catch {
      this.connectionState.set(HubConnectionState.Disconnected);
      return null;
    }
  }

  async disconnect(): Promise<void> {
    await this.connection?.stop();
    this.connectionState.set(HubConnectionState.Disconnected);
  }
}
