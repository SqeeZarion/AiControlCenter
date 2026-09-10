import { HttpErrorResponse } from '@angular/common/http';

export function directionErrorMessage(error: unknown): string {
  if (!(error instanceof HttpErrorResponse)) return 'Не вдалося виконати запит. Спробуйте ще раз.';
  if (error.status === 403) return 'Недостатньо прав для цієї дії.';
  if (error.status === 404) return 'Напрямок не знайдено.';
  if (error.status === 409)
    return error.error?.title ?? 'Дані вже змінилися. Оновіть сторінку та повторіть дію.';
  if (error.status === 400 && error.error?.errors)
    return Object.values(error.error.errors).flat().join(' ');
  return 'Не вдалося виконати запит. Спробуйте ще раз.';
}
