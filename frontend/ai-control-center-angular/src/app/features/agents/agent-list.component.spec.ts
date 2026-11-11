import { ComponentFixture, TestBed } from '@angular/core/testing';
import { convertToParamMap, ActivatedRoute, Router } from '@angular/router';
import { of } from 'rxjs';
import { AuthStore } from '../../core/auth/auth.store';
import { DirectionApiService } from '../directions/direction-api.service';
import { DirectionDto } from '../directions/direction.models';
import { RunApiService } from '../runs/run-api.service';
import { AgentRunDto } from '../runs/run.models';
import { AgentApiService } from './agent-api.service';
import { AgentListComponent } from './agent-list.component';
import { AgentDefinitionDto, AgentExecutionType } from './agent.models';

describe('AgentListComponent execution contract', () => {
  let agentApi: Record<string, ReturnType<typeof vi.fn>>;
  let directionApi: Record<string, ReturnType<typeof vi.fn>>;
  let runApi: Record<string, ReturnType<typeof vi.fn>>;
  let router: Record<string, ReturnType<typeof vi.fn>>;

  beforeEach(async () => {
    agentApi = {
      list: vi.fn(),
      changeStatus: vi.fn(),
      archive: vi.fn(),
      restore: vi.fn(),
    };
    directionApi = {
      list: vi.fn(() =>
        of({ items: [direction], page: 1, pageSize: 100, totalCount: 1, totalPages: 1 }),
      ),
    };
    runApi = { create: vi.fn(() => of({ id: 'run-id' } as AgentRunDto)) };
    router = { navigate: vi.fn(() => Promise.resolve(true)) };

    await TestBed.configureTestingModule({
      imports: [AgentListComponent],
      providers: [
        { provide: AgentApiService, useValue: agentApi },
        { provide: DirectionApiService, useValue: directionApi },
        { provide: RunApiService, useValue: runApi },
        { provide: AuthStore, useValue: { hasAnyRole: () => true } },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: convertToParamMap({}) } },
        },
        { provide: Router, useValue: router },
      ],
    }).compileComponents();
  });

  it('renders an AI prompt, hides the deterministic scenario, and sends the prompt', async () => {
    const agent = definition('Ai');
    const fixture = await render(agent);
    const text = fixture.nativeElement.textContent as string;

    expect(text).toContain('Виконання');
    expect(text).toContain('AI');
    expect(text).toContain('AI-запит');
    expect(text).not.toContain('Результат сценарію');
    expect(fixture.nativeElement.querySelector('textarea.ai-run-prompt')).not.toBeNull();

    fixture.componentInstance.setInput(agent.id, 'Поясни стан системи');
    await fixture.componentInstance.launch(agent);

    expect(runApi['create']).toHaveBeenCalledOnce();
    expect(runApi['create'].mock.calls[0]).toEqual([agent.id, 'Поясни стан системи', undefined]);
  });

  it('keeps the deterministic scenario controls for a Test agent', async () => {
    const agent = definition('Test');
    const fixture = await render(agent);
    const text = fixture.nativeElement.textContent as string;

    expect(text).toContain('Тестове');
    expect(text).toContain('Результат сценарію');
    expect(text).not.toContain('AI-запит');

    await fixture.componentInstance.launch(agent);

    expect(runApi['create']).toHaveBeenCalledOnce();
    expect(runApi['create'].mock.calls[0]).toEqual([agent.id, '', 'Succeed']);
  });

  async function render(agent: AgentDefinitionDto): Promise<ComponentFixture<AgentListComponent>> {
    agentApi['list'].mockReturnValue(
      of({ items: [agent], page: 1, pageSize: 20, totalCount: 1, totalPages: 1 }),
    );
    const fixture = TestBed.createComponent(AgentListComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }
});

const direction: DirectionDto = {
  id: '11111111-1111-1111-1111-111111111111',
  name: 'Engineering',
  code: 'engineering',
  description: null,
  icon: null,
  status: 'Active',
  sortOrder: 1,
  createdAt: '2026-11-04T00:00:00Z',
  updatedAt: '2026-11-04T00:00:00Z',
  archivedAt: null,
  version: 1,
  isArchived: false,
};

function definition(executionType: AgentExecutionType): AgentDefinitionDto {
  return {
    id: '22222222-2222-2222-2222-222222222222',
    directionId: direction.id,
    name: executionType === 'Ai' ? 'AI Agent' : 'Test Agent',
    code: executionType === 'Ai' ? 'ai-agent' : 'test-agent',
    description: null,
    status: 'Active',
    executionType,
    createdAt: '2026-11-04T00:00:00Z',
    updatedAt: '2026-11-04T00:00:00Z',
    archivedAt: null,
    version: 1,
    isArchived: false,
    systemPrompt: executionType === 'Ai' ? 'System prompt' : null,
    modelRoutesJson: executionType === 'Ai' ? '[]' : null,
  };
}
