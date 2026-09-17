import { RunStatus, RunStepStatus } from './run.models';
import { RunStatusChangedEvent } from '../../core/realtime/realtime.service';

const statusLabels: Record<RunStatus | RunStepStatus, string> = {
  Queued: 'У черзі',
  Running: 'Виконується',
  Succeeded: 'Успішно',
  Failed: 'Помилка',
};

export function runStatusLabel(status: RunStatus | RunStepStatus): string {
  return statusLabels[status];
}

export function formatUkrainianDate(value: string | null): string {
  return value
    ? new Intl.DateTimeFormat('uk-UA', { dateStyle: 'medium', timeStyle: 'medium' }).format(
        new Date(value),
      )
    : '—';
}

export function shouldRefreshRun(
  runId: string,
  currentRevision: number | null,
  event: RunStatusChangedEvent | null,
): boolean {
  return event?.runId === runId && (currentRevision === null || event.revision > currentRevision);
}
