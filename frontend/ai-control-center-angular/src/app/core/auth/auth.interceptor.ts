import { HttpContextToken, HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, from, switchMap, throwError } from 'rxjs';
import { AccessTokenStore } from './access-token.store';
import { AuthStore } from './auth.store';

const AUTH_RETRY = new HttpContextToken(() => false);
const publicAuthPaths = [
  '/api/identity/v1/auth/csrf',
  '/api/identity/v1/auth/login',
  '/api/identity/v1/auth/refresh',
  '/api/identity/v1/auth/logout',
];

export const authInterceptor: HttpInterceptorFn = (request, next) => {
  if (!isSameOrigin(request.url) || publicAuthPaths.some((path) => request.url.startsWith(path))) {
    return next(request);
  }

  const accessTokens = inject(AccessTokenStore);
  const authStore = inject(AuthStore);
  const token = accessTokens.token();
  const authenticatedRequest = token
    ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } })
    : request;

  return next(authenticatedRequest).pipe(
    catchError((error: unknown) => {
      if (!(error instanceof HttpErrorResponse) || error.status !== 401 || request.context.get(AUTH_RETRY)) {
        return throwError(() => error);
      }

      return from(authStore.refresh()).pipe(
        switchMap((refreshed) => {
          const refreshedToken = accessTokens.token();
          if (!refreshed || !refreshedToken) {
            return throwError(() => error);
          }

          return next(request.clone({
            context: request.context.set(AUTH_RETRY, true),
            setHeaders: { Authorization: `Bearer ${refreshedToken}` },
          }));
        }),
      );
    }),
  );
};

function isSameOrigin(url: string): boolean {
  if (url.startsWith('/')) {
    return true;
  }

  try {
    return new URL(url, globalThis.location.origin).origin === globalThis.location.origin;
  } catch {
    return false;
  }
}
