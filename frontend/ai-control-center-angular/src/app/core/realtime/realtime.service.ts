import { computed, inject, Injectable, InjectionToken, signal } from '@angular/core';
import {
  HubConnectionBuilder,
  HubConnectionState,
  IRetryPolicy,
  RetryContext,
} from '@microsoft/signalr';
import { AccessTokenStore } from '../auth/access-token.store';
import { APP_ENVIRONMENT } from '../configuration/app-environment';

interface TechnicalPong {
  service: string;
  timestamp: string;
}

export interface RunStatusChangedEvent {
  eventId: string;
  runId: string;
  ownerUserId: string;
  status: string;
  revision: number;
  occurredAt: string;
  stepSequence: number | null;
  stepStatus: string | null;
}

export interface RealtimeHubConnection {
  readonly state: HubConnectionState;
  start(): Promise<void>;
  stop(): Promise<void>;
  invoke<T>(methodName: string): Promise<T>;
  on(methodName: 'RunStatusChanged', handler: (event: RunStatusChangedEvent) => void): void;
  onreconnecting(handler: (error?: Error) => void): void;
  onreconnected(handler: (connectionId?: string) => void): void;
  onclose(handler: (error?: Error) => void): void;
}

export interface RealtimeHubConnectionFactory {
  create(
    url: string,
    accessTokenFactory: () => string,
    retryPolicy: IRetryPolicy,
  ): RealtimeHubConnection;
}

export const REALTIME_HUB_CONNECTION_FACTORY = new InjectionToken<RealtimeHubConnectionFactory>(
  'REALTIME_HUB_CONNECTION_FACTORY',
  {
    providedIn: 'root',
    factory: () => ({
      create: (url, accessTokenFactory, retryPolicy) =>
        new HubConnectionBuilder()
          .withUrl(url, { accessTokenFactory })
          .withAutomaticReconnect(retryPolicy)
          .build(),
    }),
  },
);

const reconnectDelaysMilliseconds = [0, 2_000, 5_000, 10_000, 30_000] as const;

export function realtimeReconnectDelay(previousRetryCount: number): number {
  return reconnectDelaysMilliseconds[
    Math.min(previousRetryCount, reconnectDelaysMilliseconds.length - 1)
  ];
}

@Injectable({ providedIn: 'root' })
export class RealtimeService {
  private readonly config = inject(APP_ENVIRONMENT);
  private readonly accessTokens = inject(AccessTokenStore);
  private readonly connectionFactory = inject(REALTIME_HUB_CONNECTION_FACTORY);
  private readonly connectionState = signal<HubConnectionState>(HubConnectionState.Disconnected);
  private readonly latestRunEventValue = signal<RunStatusChangedEvent | null>(null);
  private readonly reconnectGenerationValue = signal(0);
  private connection?: RealtimeHubConnection;
  private retryTimer?: ReturnType<typeof setTimeout>;
  private initialRetryCount = 0;
  private connectionGeneration = 0;
  private shouldReconnect = false;
  private startInFlight?: Promise<TechnicalPong | null>;

  readonly state = this.connectionState.asReadonly();
  readonly isConnected = computed(() => this.connectionState() === HubConnectionState.Connected);
  readonly latestRunEvent = this.latestRunEventValue.asReadonly();
  readonly reconnectGeneration = this.reconnectGenerationValue.asReadonly();

  async connect(): Promise<TechnicalPong | null> {
    if (!this.accessTokens.token()) return null;

    this.shouldReconnect = true;
    if (this.connection?.state === HubConnectionState.Connected) {
      return this.connection.invoke<TechnicalPong>('Ping').catch(() => null);
    }
    if (this.startInFlight) return this.startInFlight;

    if (!this.connection) this.createConnection();
    return this.startConnection(this.connectionGeneration);
  }

  async disconnect(): Promise<void> {
    this.shouldReconnect = false;
    this.connectionGeneration += 1;
    this.initialRetryCount = 0;
    if (this.retryTimer) {
      clearTimeout(this.retryTimer);
      this.retryTimer = undefined;
    }

    const connection = this.connection;
    this.connection = undefined;
    this.startInFlight = undefined;
    await connection?.stop();
    this.connectionState.set(HubConnectionState.Disconnected);
  }

  async reconnectWithFreshToken(): Promise<TechnicalPong | null> {
    await this.disconnect();
    return this.connect();
  }

  private createConnection(): void {
    const generation = ++this.connectionGeneration;
    const connection = this.connectionFactory.create(
      `${this.config.gatewayBaseUrl}${this.config.systemHubPath}`,
      () => this.accessTokens.token() ?? '',
      {
        nextRetryDelayInMilliseconds: (context: RetryContext) =>
          realtimeReconnectDelay(context.previousRetryCount),
      },
    );

    connection.on('RunStatusChanged', (event: RunStatusChangedEvent) => {
      if (generation === this.connectionGeneration) this.latestRunEventValue.set(event);
    });
    connection.onreconnecting(() => {
      if (generation === this.connectionGeneration)
        this.connectionState.set(HubConnectionState.Reconnecting);
    });
    connection.onreconnected(() => {
      if (generation === this.connectionGeneration) {
        this.initialRetryCount = 0;
        this.connectionState.set(HubConnectionState.Connected);
        this.reconnectGenerationValue.update((value) => value + 1);
      }
    });
    connection.onclose(() => {
      if (generation === this.connectionGeneration) {
        this.connectionState.set(HubConnectionState.Disconnected);
        this.scheduleInitialRetry(generation);
      }
    });

    this.connection = connection;
  }

  private startConnection(generation: number): Promise<TechnicalPong | null> {
    const connection = this.connection;
    if (!connection || generation !== this.connectionGeneration) return Promise.resolve(null);

    this.connectionState.set(HubConnectionState.Connecting);
    const operation = connection
      .start()
      .then(async () => {
        if (generation !== this.connectionGeneration) return null;
        this.initialRetryCount = 0;
        this.connectionState.set(HubConnectionState.Connected);
        return connection.invoke<TechnicalPong>('Ping').catch(() => null);
      })
      .catch(() => {
        if (generation === this.connectionGeneration) {
          this.connectionState.set(HubConnectionState.Disconnected);
          this.scheduleInitialRetry(generation);
        }
        return null;
      })
      .finally(() => {
        if (this.startInFlight === operation) this.startInFlight = undefined;
      });

    this.startInFlight = operation;
    return operation;
  }

  private scheduleInitialRetry(generation: number): void {
    if (
      !this.shouldReconnect ||
      !this.accessTokens.token() ||
      generation !== this.connectionGeneration ||
      this.retryTimer
    ) {
      return;
    }

    const delay = realtimeReconnectDelay(this.initialRetryCount++);
    this.retryTimer = setTimeout(() => {
      this.retryTimer = undefined;
      if (this.shouldReconnect && generation === this.connectionGeneration)
        void this.startConnection(generation);
    }, delay);
  }
}
