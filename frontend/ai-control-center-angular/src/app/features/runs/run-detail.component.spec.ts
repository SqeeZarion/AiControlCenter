import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { signal } from '@angular/core';
import { of, Subject } from 'rxjs';
import { RealtimeService, RunStatusChangedEvent } from '../../core/realtime/realtime.service';
import { RunApiService } from './run-api.service';
import { RunDetailComponent } from './run-detail.component';
import { AgentRunDto, AiProviderAttemptDto, RunStatus } from './run.models';

describe('RunDetailComponent reconciliation', () => {
  let fixture: ComponentFixture<RunDetailComponent>;
  let latestEvent: ReturnType<typeof signal<RunStatusChangedEvent | null>>;
  let reconnectGeneration: ReturnType<typeof signal<number>>;
  let requests: Subject<AgentRunDto>[];
  let api: {
    get: ReturnType<typeof vi.fn>;
    preflight: ReturnType<typeof vi.fn>;
    resume: ReturnType<typeof vi.fn>;
  };

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
      preflight: vi.fn(() => of({ available: false, code: 'ProviderUnavailable' })),
      resume: vi.fn(),
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

  it('shows typed pre-dispatch diagnostics and a terminal failed step', () => {
    resolveRequest(0, {
      ...run(5, 'Failed'),
      error: 'AI provider rejected the run (Configuration).',
      steps: [
        {
          id: 'step-1',
          sequence: 1,
          name: 'AI execution',
          status: 'Failed',
          log: 'SessionCreate failed before generation dispatch.',
          startedAt: '2026-09-12T12:00:01Z',
          updatedAt: '2026-09-12T12:00:03Z',
          completedAt: '2026-09-12T12:00:03Z',
        },
      ],
      attempts: [
        {
          id: 'attempt-1',
          sequence: 1,
          provider: 'GitHubCopilotLocalCli',
          model: 'Auto',
          status: 'Failed',
          failureKind: 'Configuration',
          providerRequestId: null,
          inputTokens: 0,
          outputTokens: 0,
          estimatedCostUsd: null,
          actualModel: null,
          idempotencyKey: 'safe-idempotency-key',
          routePriority: 1,
          latencyMilliseconds: 170,
          cachedInputTokens: null,
          totalTokens: 0,
          usageSource: 'Unavailable',
          costSource: 'Unavailable',
          finishStatus: 'Rejected',
          fallbackReason: 'Configuration',
          partialOutput: null,
          startedAt: '2026-09-12T12:00:01Z',
          completedAt: '2026-09-12T12:00:03Z',
          errorCode: 'session_option_unsupported',
          operation: 'SessionCreate',
          generationDispatched: 'false',
          safeExceptionType: 'ProviderRuntimeException',
        },
      ],
    });

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Operation: SessionCreate');
    expect(text).toContain('Safe code: session_option_unsupported');
    expect(text).toContain('не підтримує один із параметрів сесії');
    expect(text).toContain('Generation dispatched: Ні');
    expect(text).toContain('Помилка');
    expect(fixture.nativeElement.querySelector('button.primary')).toBeNull();
  });

  it('shows unavailable usage instead of synthetic zeroes when the provider did not report usage', () => {
    resolveRequest(0, {
      ...run(6, 'Succeeded'),
      attempts: [attempt('', 'true', 'None')],
    });

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain(
      'Tokens: input Unavailable, cached Unavailable, output Unavailable, total Unavailable',
    );
    expect(text).toContain('Cost: Unavailable');
    expect(text).toContain('Actual: not reported');
    expect(text).not.toContain('Tokens: input 0');
  });

  it('keeps provider-confirmed numeric zeroes as zeroes', () => {
    resolveRequest(0, {
      ...run(6, 'Succeeded'),
      attempts: [
        {
          ...attempt('', 'true', 'None'),
          cachedInputTokens: 0,
          usageSource: 'ProviderReported',
          costSource: 'ProviderReported',
          estimatedCostUsd: 0,
        },
      ],
    });

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Tokens: input 0, cached 0, output 0, total 0');
    expect(text).toContain('Cost: 0 USD');
  });

  it('distinguishes a deterministic pre-dispatch rejection from an unknown outcome', () => {
    expect(
      fixture.componentInstance.attemptDiagnosticMessage(
        attempt('generation_model_rejected', 'false', 'ModelUnavailable'),
      ),
    ).toContain('відхилив модель до виконання generation');
    expect(
      fixture.componentInstance.attemptDiagnosticMessage(
        attempt('generation_outcome_ambiguous', 'unknown', 'AmbiguousTimeout'),
      ),
    ).toContain('Результат provider request невідомий');
  });

  it('keeps resume disabled until a paused provider run passes preflight', () => {
    resolveRequest(0, {
      ...run(8, 'PausedProviderUnavailable'),
      steps: [
        {
          id: 'step-1',
          sequence: 1,
          name: 'AI execution',
          status: 'Paused',
          log: 'Provider unavailable.',
          startedAt: '2026-09-12T12:00:01Z',
          updatedAt: '2026-09-12T12:00:02Z',
          completedAt: null,
        },
      ],
    });

    const button = fixture.nativeElement.querySelector('button.primary') as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    expect(fixture.nativeElement.textContent).toContain('Призупинено');
    expect(api.resume).not.toHaveBeenCalled();
  });

  it('enables resume only after a successful provider preflight', () => {
    api.preflight.mockReturnValue(of({ available: true, code: 'Available' }));
    resolveRequest(0, run(8, 'PausedProviderUnavailable'));

    const button = fixture.nativeElement.querySelector('button.primary') as HTMLButtonElement;
    expect(api.preflight).toHaveBeenCalledOnce();
    expect(button.disabled).toBe(false);
  });

  it('blocks Resume and explains a legacy ambiguous Local CLI outcome', () => {
    api.preflight.mockReturnValue(of({ available: false, code: 'LegacyAmbiguousOutcome' }));
    resolveRequest(0, run(8, 'PausedProviderUnavailable'));

    expect(api.preflight).toHaveBeenCalledOnce();
    expect(fixture.nativeElement.querySelector('button.primary')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Результат попереднього запиту невідомий');
    expect(fixture.nativeElement.textContent).toContain(
      'Повторне виконання цього Run заблоковано для запобігання подвійному списанню',
    );
    expect(fixture.nativeElement.textContent).toContain('створити новий Run');
    expect(api.resume).not.toHaveBeenCalled();
  });

  it('sends exactly one resume mutation for a double click while the request is pending', async () => {
    const resumeResponse = new Subject<AgentRunDto>();
    api.preflight.mockReturnValue(of({ available: true, code: 'Available' }));
    api.resume.mockReturnValue(resumeResponse);
    resolveRequest(0, run(8, 'PausedProviderUnavailable'));

    const first = fixture.componentInstance.resume();
    const second = fixture.componentInstance.resume();

    expect(api.resume).toHaveBeenCalledOnce();
    const [runId, operationId] = api.resume.mock.calls[0] as [string, string];
    expect(runId).toBe('run-1');
    expect(operationId).toMatch(/^[0-9a-f-]{36}$/i);
    expect(fixture.componentInstance.resumePending()).toBe(true);

    resumeResponse.next(run(9, 'Queued'));
    resumeResponse.complete();
    await Promise.all([first, second]);
    expect(fixture.componentInstance.resumePending()).toBe(false);
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

function attempt(
  errorCode: string,
  generationDispatched: string,
  failureKind: string,
): AiProviderAttemptDto {
  return {
    id: 'attempt-safe',
    sequence: 1,
    provider: 'GitHubCopilotLocalCli',
    model: 'Auto',
    status: 'Failed',
    failureKind,
    providerRequestId: null,
    inputTokens: 0,
    outputTokens: 0,
    estimatedCostUsd: null,
    actualModel: null,
    idempotencyKey: 'safe-idempotency-key',
    routePriority: 1,
    latencyMilliseconds: 20,
    cachedInputTokens: null,
    totalTokens: 0,
    usageSource: 'Unavailable',
    costSource: 'Unavailable',
    finishStatus: 'Rejected',
    fallbackReason: failureKind,
    partialOutput: null,
    startedAt: '2026-11-09T14:47:18Z',
    completedAt: '2026-11-09T14:47:19Z',
    errorCode,
    operation: 'GenerationDispatch',
    generationDispatched,
    safeExceptionType: 'RemoteRpcException',
  };
}
