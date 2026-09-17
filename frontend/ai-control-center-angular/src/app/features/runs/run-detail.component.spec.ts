import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { signal } from '@angular/core';
import { Subject } from 'rxjs';
import { RealtimeService, RunStatusChangedEvent } from '../../core/realtime/realtime.service';
import { RunApiService } from './run-api.service';
import { RunDetailComponent } from './run-detail.component';
import { AgentRunDto, RunStatus } from './run.models';

describe('RunDetailComponent reconciliation', () => {
  let fixture: ComponentFixture<RunDetailComponent>;
  let latestEvent: ReturnType<typeof signal<RunStatusChangedEvent | null>>;
  let reconnectGeneration: ReturnType<typeof signal<number>>;
  let requests: Subject<AgentRunDto>[];
  let api: { get: ReturnType<typeof vi.fn> };

  beforeEach(async () => {
    latestEvent = signal<RunStatusChangedEvent | null>(null);
    reconnectGeneration = signal(0);
    requests = [];
    api = {
      get: vi.fn(() => {
        const request = new Subject<AgentRunDto>();
        requests.push(request);
        return request;
      }),
    };
    await TestBed.configureTestingModule({
      imports: [RunDetailComponent],
      providers: [
        provideRouter([]),
        { provide: RunApiService, useValue: api },
        {
          provide: RealtimeService,
          useValue: {
            latestRunEvent: latestEvent.asReadonly(),
            reconnectGeneration: reconnectGeneration.asReadonly(),
          },
        },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: convertToParamMap({ id: 'run-1' }) } },
        },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(RunDetailComponent);
    fixture.detectChanges();
  });

  it('never lets an older response overwrite a newer revision or terminal state', async () => {
    resolveRequest(0, run(4));
    emit(5);
    emit(6);
    expect(requests).toHaveLength(3);

    resolveRequest(2, run(6, 'Succeeded'));
    resolveRequest(1, run(5, 'Running'));

    expect(fixture.componentInstance.run()?.revision).toBe(6);
    expect(fixture.componentInstance.run()?.status).toBe('Succeeded');
  });

  it('does not start a duplicate GET for the same requested revision', () => {
    resolveRequest(0, run(4));
    emit(5);
    emit(5);
    expect(requests).toHaveLength(2);
  });

  it('requests the highest revision received while earlier requests are in flight', () => {
    resolveRequest(0, run(4));
    emit(5);
    emit(6);
    emit(7);
    expect(requests).toHaveLength(4);

    resolveRequest(3, run(7));
    resolveRequest(1, run(5));
    resolveRequest(2, run(6));
    expect(fixture.componentInstance.run()?.revision).toBe(7);
  });

  it('ignores a stale event after a newer revision has been applied', () => {
    resolveRequest(0, run(5));
    emit(4);
    expect(requests).toHaveLength(1);
  });

  it('performs one reconciliation per reconnect generation', () => {
    resolveRequest(0, run(4));
    reconnectGeneration.set(1);
    fixture.detectChanges();
    expect(requests).toHaveLength(2);
    reconnectGeneration.set(1);
    fixture.detectChanges();
    expect(requests).toHaveLength(2);
  });

  it('recovers from an HTTP error when a newer revision arrives', () => {
    resolveRequest(0, run(4));
    emit(5);
    requests[1].error(new Error('network'));
    emit(6);
    expect(requests).toHaveLength(3);
    resolveRequest(2, run(6));
    expect(fixture.componentInstance.run()?.revision).toBe(6);
  });

  it('cancels pending requests when destroyed', () => {
    resolveRequest(0, run(4));
    emit(5);
    fixture.destroy();

    requests[1].next(run(5));
    requests[1].complete();
    expect(fixture.componentInstance.run()?.revision).toBe(4);
  });

  function emit(revision: number): void {
    latestEvent.set({
      eventId: `event-${revision}-${requests.length}`,
      runId: 'run-1',
      ownerUserId: 'owner',
      status: 'Running',
      revision,
      occurredAt: '2026-09-12T12:00:00Z',
      stepSequence: null,
      stepStatus: null,
    });
    fixture.detectChanges();
  }

  function resolveRequest(index: number, value: AgentRunDto): void {
    requests[index].next(value);
    requests[index].complete();
    fixture.detectChanges();
  }
});

function run(revision: number, status: RunStatus = 'Running'): AgentRunDto {
  return {
    id: 'run-1',
    agentId: 'agent-1',
    ownerUserId: 'owner',
    correlationId: 'correlation',
    agentName: 'Test Agent',
    agentCode: 'test-agent',
    agentVersion: 1,
    directionId: 'direction-1',
    directionName: 'Direction',
    directionCode: 'direction',
    directionVersion: 1,
    executionType: 'Test',
    input: '',
    expectedOutcome: 'Succeed',
    status,
    revision,
    createdAt: '2026-09-12T12:00:00Z',
    startedAt: '2026-09-12T12:00:01Z',
    completedAt: status === 'Running' ? null : '2026-09-12T12:00:10Z',
    result: status === 'Succeeded' ? 'ok' : null,
    error: null,
    steps: [],
  };
}
