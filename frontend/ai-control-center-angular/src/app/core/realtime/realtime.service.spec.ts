import { TestBed } from '@angular/core/testing';
import { HubConnectionState, IRetryPolicy } from '@microsoft/signalr';
import { AccessTokenStore } from '../auth/access-token.store';
import {
  REALTIME_HUB_CONNECTION_FACTORY,
  RealtimeHubConnection,
  RealtimeHubConnectionFactory,
  RealtimeService,
  realtimeReconnectDelay,
  RunStatusChangedEvent,
} from './realtime.service';

describe('RealtimeService', () => {
  let connection: FakeRealtimeHubConnection;
  let factory: FakeRealtimeHubConnectionFactory;
  let service: RealtimeService;
  let accessTokens: AccessTokenStore;

  beforeEach(() => {
    vi.useFakeTimers();
    connection = new FakeRealtimeHubConnection();
    factory = new FakeRealtimeHubConnectionFactory(connection);
    TestBed.configureTestingModule({
      providers: [{ provide: REALTIME_HUB_CONNECTION_FACTORY, useValue: factory }],
    });
    accessTokens = TestBed.inject(AccessTokenStore);
    accessTokens.set('memory-only-token');
    service = TestBed.inject(RealtimeService);
  });

  afterEach(() => vi.useRealTimers());

  it('uses bounded delays and caps retries at 30 seconds', () => {
    expect([0, 1, 2, 3, 4, 5, 20].map(realtimeReconnectDelay)).toEqual([
      0, 2_000, 5_000, 10_000, 30_000, 30_000, 30_000,
    ]);
  });

  it('connects once and processes one event after an automatic reconnect', async () => {
    await service.connect();

    expect(factory.createCalls).toBe(1);
    expect(connection.startCalls).toBe(1);
    expect(connection.runEventHandlerRegistrations).toBe(1);
    expect(service.state()).toBe(HubConnectionState.Connected);
    expect(connection.accessTokenFactory?.()).toBe('memory-only-token');

    connection.triggerReconnecting();
    expect(service.state()).toBe(HubConnectionState.Reconnecting);
    connection.triggerReconnected();

    expect(service.state()).toBe(HubConnectionState.Connected);
    expect(service.reconnectGeneration()).toBe(1);
    const event = runEvent('event-after-reconnect');
    connection.emitRunEvent(event);

    expect(connection.runEventDeliveries).toBe(1);
    expect(connection.runEventHandlerRegistrations).toBe(1);
    expect(service.latestRunEvent()).toBe(event);
    expect(accessTokens.token()).toBe('memory-only-token');
  });

  it('retries a closed initial connection with deterministic bounded backoff', async () => {
    await service.connect();
    connection.failedStartsRemaining = 2;

    connection.triggerClose();
    expect(service.state()).toBe(HubConnectionState.Disconnected);
    await vi.advanceTimersByTimeAsync(0);
    expect(connection.startCalls).toBe(2);

    await vi.advanceTimersByTimeAsync(1_999);
    expect(connection.startCalls).toBe(2);
    await vi.advanceTimersByTimeAsync(1);
    expect(connection.startCalls).toBe(3);

    await vi.advanceTimersByTimeAsync(4_999);
    expect(connection.startCalls).toBe(3);
    await vi.advanceTimersByTimeAsync(1);

    expect(connection.startCalls).toBe(4);
    expect(service.state()).toBe(HubConnectionState.Connected);
    expect(accessTokens.token()).toBe('memory-only-token');
  });
});

function runEvent(eventId: string): RunStatusChangedEvent {
  return {
    eventId,
    runId: 'run-1',
    ownerUserId: 'owner-1',
    status: 'Running',
    revision: 2,
    occurredAt: '2026-10-05T00:00:00Z',
    stepSequence: 1,
    stepStatus: 'Running',
  };
}

class FakeRealtimeHubConnectionFactory implements RealtimeHubConnectionFactory {
  createCalls = 0;

  constructor(private readonly connection: FakeRealtimeHubConnection) {}

  create(
    _url: string,
    accessTokenFactory: () => string,
    retryPolicy: IRetryPolicy,
  ): RealtimeHubConnection {
    this.createCalls += 1;
    this.connection.accessTokenFactory = accessTokenFactory;
    this.connection.retryPolicy = retryPolicy;
    return this.connection;
  }
}

class FakeRealtimeHubConnection implements RealtimeHubConnection {
  state = HubConnectionState.Disconnected;
  startCalls = 0;
  failedStartsRemaining = 0;
  runEventHandlerRegistrations = 0;
  runEventDeliveries = 0;
  accessTokenFactory?: () => string;
  retryPolicy?: IRetryPolicy;
  private runEventHandler?: (event: RunStatusChangedEvent) => void;
  private reconnectingHandler?: (error?: Error) => void;
  private reconnectedHandler?: (connectionId?: string) => void;
  private closeHandler?: (error?: Error) => void;

  start(): Promise<void> {
    this.startCalls += 1;
    if (this.failedStartsRemaining > 0) {
      this.failedStartsRemaining -= 1;
      this.state = HubConnectionState.Disconnected;
      return Promise.reject(new Error('offline'));
    }
    this.state = HubConnectionState.Connected;
    return Promise.resolve();
  }

  stop(): Promise<void> {
    this.state = HubConnectionState.Disconnected;
    return Promise.resolve();
  }

  invoke<T>(_methodName: string): Promise<T> {
    return Promise.resolve({ service: 'gateway', timestamp: '2026-10-05T00:00:00Z' } as T);
  }

  on(_methodName: 'RunStatusChanged', handler: (event: RunStatusChangedEvent) => void): void {
    this.runEventHandlerRegistrations += 1;
    this.runEventHandler = handler;
  }

  onreconnecting(handler: (error?: Error) => void): void {
    this.reconnectingHandler = handler;
  }

  onreconnected(handler: (connectionId?: string) => void): void {
    this.reconnectedHandler = handler;
  }

  onclose(handler: (error?: Error) => void): void {
    this.closeHandler = handler;
  }

  triggerReconnecting(): void {
    this.state = HubConnectionState.Reconnecting;
    this.reconnectingHandler?.(new Error('temporary outage'));
  }

  triggerReconnected(): void {
    this.state = HubConnectionState.Connected;
    this.reconnectedHandler?.('connection-2');
  }

  triggerClose(): void {
    this.state = HubConnectionState.Disconnected;
    this.closeHandler?.(new Error('connection closed'));
  }

  emitRunEvent(event: RunStatusChangedEvent): void {
    this.runEventDeliveries += 1;
    this.runEventHandler?.(event);
  }
}
