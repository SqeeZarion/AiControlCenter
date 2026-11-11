import { HttpErrorResponse } from '@angular/common/http';

const codeMessages: Record<string, string> = {
  AgentRouteConnectionUnavailable:
    'Вибране підключення недоступне, неактивне або належить іншому користувачу.',
  AgentRouteInvalid: 'Конфігурація AI-маршруту некоректна або модель недоступна.',
  AgentRouteValidationUnavailable:
    'Перевірка підключення AI-провайдера тимчасово недоступна. Повторіть спробу пізніше.',
  AgentRouteOwnerMissing: 'Не вдалося визначити власника AI-маршруту.',
};

export function agentErrorMessage(error: unknown): string {
  if (!(error instanceof HttpErrorResponse)) return 'Не вдалося виконати запит. Спробуйте ще раз.';
  const problem = error.error as
    | {
        code?: string;
        field?: string;
        title?: string;
        correlationId?: string;
        traceId?: string;
        errors?: Record<string, string[]>;
      }
    | undefined;
  const message =
    (problem?.code && codeMessages[problem.code]) ||
    (problem?.errors ? Object.values(problem.errors).flat().join(' ') : '') ||
    problem?.title ||
    (error.status === 403 ? 'Недостатньо прав для цієї дії.' : '') ||
    'Не вдалося виконати запит. Спробуйте ще раз.';
  const correlationId = problem?.correlationId ?? problem?.traceId;
  return correlationId ? `${message} Correlation ID: ${correlationId}` : message;
}
