import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { HubConnectionState, IRetryPolicy } from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { AccessTokenStore } from '../../core/auth/access-token.store';
import {
  REALTIME_HUB_CONNECTION_FACTORY,
  RealtimeHubConnection,
  RealtimeHubConnectionFactory,
  RealtimeService,
  RunStatusChangedEvent,
} from '../../core/realtime/realtime.service';
import { RunApiService } from '../runs/run-api.service';
import { AgentRunDto } from '../runs/run.models';
import { OperationsApiService } from './operations-api.service';
import { OperationsCenterComponent } from './operations-center.component';
import { OperationsCenterSnapshotDto } from './operations.models';

describe('OperationsCenterComponent', () => {
  let fixture: ComponentFixture<OperationsCenterComponent>;
  let requests: Subject<OperationsCenterSnapshotDto>[];
  let runRequests: Subject<AgentRunDto>[];
  let connection: OperationsRealtimeConnection;
  let realtime: RealtimeService;
  let accessTokens: AccessTokenStore;

  beforeEach(async () => {
    requests = [];
    runRequests = [];
    connection = new OperationsRealtimeConnection();
    await TestBed.configureTestingModule({
      imports: [OperationsCenterComponent],
      providers: [
        provideRouter([]),
        {
          provide: OperationsApiService,
          useValue: {
            getSnapshot: vi.fn(() => {
              const request = new Subject<OperationsCenterSnapshotDto>();
              requests.push(request);
              return request;
            }),
          },
        },
        {
          provide: RunApiService,
          useValue: {
            get: vi.fn(() => {
              const request = new Subject<AgentRunDto>();
              runRequests.push(request);
              return request;
            }),
          },
        },
        {
          provide: REALTIME_HUB_CONNECTION_FACTORY,
          useValue: new OperationsRealtimeFactory(connection),
        },
      ],
    }).compileComponents();
    accessTokens = TestBed.inject(AccessTokenStore);
    accessTokens.set('memory-only-token');
    realtime = TestBed.inject(RealtimeService);
    await realtime.connect();
    fixture = TestBed.createComponent(OperationsCenterComponent);
    fixture.detectChanges();
  });

  afterEach(async () => {
    fixture.destroy();
    await realtime.disconnect();
  });

  it('preserves the last snapshot during an outage and reconciles after a live event', async () => {
    const initial = snapshot();
    requests[0].next(initial);
    requests[0].complete();
    await flushMicrotasks();
    fixture.detectChanges();

    fixture.componentInstance.refreshNow();
    await flushMicrotasks();
    requests[1].error(new Error('network unavailable'));
    await flushMicrotasks();
    fixture.detectChanges();

    expect(fixture.componentInstance.snapshot()).toBe(initial);
    expect(fixture.componentInstance.state()).toBe('Degraded');
    expect(fixture.nativeElement.textContent).toContain('Останні відомі дані');

    connection.emitRunEvent({
      eventId: 'event-1',
      runId: 'run-1',
      ownerUserId: 'owner-1',
      status: 'Running',
      revision: 2,
      occurredAt: '2026-09-30T12:00:01Z',
      stepSequence: 1,
      stepStatus: 'Running',
    });
    fixture.detectChanges();
    await flushMicrotasks();

    expect(requests).toHaveLength(3);
  });

  it('reconciles through REST after a SignalR reconnect', async () => {
    requests[0].next(snapshot());
    requests[0].complete();
    await flushMicrotasks();
    connection.triggerReconnected();
    fixture.detectChanges();
    await flushMicrotasks();

    expect(requests).toHaveLength(2);
    expect(accessTokens.token()).toBe('memory-only-token');
  });

  it('replaces details when the newly selected run has a lower revision', async () => {
    const first = run('run-1', 10, 'First Agent');
    const second = run('run-2', 2, 'Second Agent');

    fixture.componentInstance.selectRun(first.id);
    runRequests[0].next(first);
    runRequests[0].complete();
    await flushMicrotasks();
    expect(fixture.componentInstance.selectedRun()).toBe(first);

    fixture.componentInstance.selectRun(second.id);
    expect(fixture.componentInstance.selectedRun()).toBeNull();
    runRequests[1].next(second);
    runRequests[1].complete();
    await flushMicrotasks();

    expect(fixture.componentInstance.selectedRun()).toBe(second);
  });

  it('ignores a late response from the previously selected run', async () => {
    requests[0].next(snapshot());
    requests[0].complete();
    await flushMicrotasks();
    const first = run('run-1', 10, 'First Agent');
    const second = run('run-2', 2, 'Second Agent');

    fixture.componentInstance.selectRun(first.id);
    fixture.componentInstance.selectRun(second.id);
    runRequests[1].next(second);
    runRequests[1].complete();
    await flushMicrotasks();
    runRequests[0].next(first);
    runRequests[0].complete();
    await flushMicrotasks();
    fixture.detectChanges();

    expect(fixture.componentInstance.selectedRun()).toBe(second);
    const detailsLink = fixture.nativeElement.querySelector(
      '.run-inspector-heading a',
    ) as HTMLAnchorElement | null;
    expect(detailsLink?.getAttribute('href')).toBe('/runs/run-2');
  });

  it('rejects a stale revision of the currently selected run', async () => {
    const current = run('run-1', 10, 'Current Agent');
    const stale = run('run-1', 9, 'Stale Agent');

    fixture.componentInstance.selectRun(current.id);
    runRequests[0].next(current);
    runRequests[0].complete();
    await flushMicrotasks();
    fixture.componentInstance.selectRun(stale.id);
    runRequests[1].next(stale);
    runRequests[1].complete();
    await flushMicrotasks();

    expect(fixture.componentInstance.selectedRun()).toBe(current);
  });

  it('rejects a response whose id does not match the selected run', async () => {
    fixture.componentInstance.selectRun('run-2');
    runRequests[0].next(run('run-1', 20, 'Unexpected Agent'));
    runRequests[0].complete();
    await flushMicrotasks();

    expect(fixture.componentInstance.selectedRun()).toBeNull();
  });

  it('does not restore the previous run details when the new request fails', async () => {
    const first = run('run-1', 10, 'First Agent');

    fixture.componentInstance.selectRun(first.id);
    runRequests[0].next(first);
    runRequests[0].complete();
    await flushMicrotasks();
    fixture.componentInstance.selectRun('run-2');
    runRequests[1].error(new Error('run unavailable'));
    await flushMicrotasks();

    expect(fixture.componentInstance.selectedRun()).toBeNull();
    expect(fixture.componentInstance.runError()).toBe('Деталі Run тимчасово недоступні.');
  });
});

function snapshot(): OperationsCenterSnapshotDto {
  const timestamp = new Date().toISOString();
  return {
    generatedAt: timestamp,
    freshnessSeconds: 30,
    nodes: [
      {
        id: 'gateway',
        label: 'Gateway',
        kind: 'service',
        state: 'Connected',
        latencyMilliseconds: 2,
        lastCheckedAt: timestamp,
        lastSuccessfulAt: timestamp,
        checks: [],
      },
    ],
    queues: [],
    workload: {
      totalAgents: 1,
      activeAgents: 1,
      queuedRuns: 0,
      runningRuns: 0,
      succeededRuns: 0,
      failedRuns: 0,
      recentRuns: [],
    },
  };
}

function run(id: string, revision: number, agentName: string): AgentRunDto {
  const timestamp = new Date().toISOString();
  return {
    id,
    agentId: 'agent-1',
    ownerUserId: 'owner-1',
    correlationId: `correlation-${id}`,
    agentName,
    agentCode: agentName.toLowerCase().replace(' ', '-'),
    agentVersion: 1,
    directionId: 'direction-1',
    directionName: 'Direction',
    directionCode: 'direction',
    directionVersion: 1,
    executionType: 'Deterministic',
    input: '',
    expectedOutcome: 'Succeed',
    status: 'Succeeded',
    revision,
    createdAt: timestamp,
    startedAt: timestamp,
    completedAt: timestamp,
    result: 'Completed',
    error: null,
    steps: [],
  };
}

async function flushMicrotasks(): Promise<void> {
  await Promise.resolve();
  await Promise.resolve();
}

class OperationsRealtimeFactory implements RealtimeHubConnectionFactory {
  constructor(private readonly connection: OperationsRealtimeConnection) {}

  create(
    _url: string,
    _accessTokenFactory: () => string,
    _retryPolicy: IRetryPolicy,
  ): RealtimeHubConnection {
    return this.connection;
  }
}

class OperationsRealtimeConnection implements RealtimeHubConnection {
  state = HubConnectionState.Disconnected;
  private runEventHandler?: (event: RunStatusChangedEvent) => void;
  private reconnectedHandler?: (connectionId?: string) => void;

  start(): Promise<void> {
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
    this.runEventHandler = handler;
  }

  onreconnecting(_handler: (error?: Error) => void): void {}

  onreconnected(handler: (connectionId?: string) => void): void {
    this.reconnectedHandler = handler;
  }

  onclose(_handler: (error?: Error) => void): void {}

  triggerReconnected(): void {
    this.state = HubConnectionState.Connected;
    this.reconnectedHandler?.('connection-2');
  }

  emitRunEvent(event: RunStatusChangedEvent): void {
    this.runEventHandler?.(event);
  }
}
