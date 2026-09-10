import { HttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { RealtimeService } from '../realtime/realtime.service';
import { AccessTokenStore } from './access-token.store';
import { authInterceptor } from './auth.interceptor';
import { AuthSessionDto } from './auth.models';

describe('session expiration flow', () => {
  const refreshedSession: AuthSessionDto = {
    accessToken: 'token-b',
    tokenType: 'Bearer',
    expiresInSeconds: 600,
    user: {
      id: '11111111-1111-4111-8111-111111111111',
      email: 'admin@example.test',
      displayName: 'Admin',
      roles: ['Admin'],
      mustChangePassword: false,
    },
  };

  it('uses one failed refresh and one login redirect for parallel protected 401 responses', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: RealtimeService,
          useValue: {
            reconnectWithFreshToken: () => Promise.resolve(null),
            disconnect: () => Promise.resolve(),
          },
        },
      ],
    });
    const client = TestBed.inject(HttpClient);
    const http = TestBed.inject(HttpTestingController);
    const tokens = TestBed.inject(AccessTokenStore);
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    tokens.set('expired-token');

    const first = firstValueFrom(client.get('/api/first'));
    const second = firstValueFrom(client.get('/api/second'));
    http.expectOne('/api/first').flush(null, { status: 401, statusText: 'Unauthorized' });
    http.expectOne('/api/second').flush(null, { status: 401, statusText: 'Unauthorized' });
    await Promise.resolve();
    http.expectOne('/api/identity/v1/auth/refresh').flush(null, {
      status: 401,
      statusText: 'Unauthorized',
    });

    await expect(first).rejects.toBeTruthy();
    await expect(second).rejects.toBeTruthy();
    expect(tokens.token()).toBeNull();
    expect(navigate).toHaveBeenCalledTimes(1);
    http.verify();
  });

  it('retries a staggered old-token 401 with token B without a second refresh', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: RealtimeService,
          useValue: {
            reconnectWithFreshToken: () => Promise.resolve(null),
            disconnect: () => Promise.resolve(),
          },
        },
      ],
    });
    const client = TestBed.inject(HttpClient);
    const http = TestBed.inject(HttpTestingController);
    const tokens = TestBed.inject(AccessTokenStore);
    tokens.set('token-a');

    const first = firstValueFrom(client.get<{ request: string }>('/api/first'));
    const second = firstValueFrom(client.get<{ request: string }>('/api/second'));
    const firstInitial = http.expectOne('/api/first');
    const secondInitial = http.expectOne('/api/second');
    expect(firstInitial.request.headers.get('Authorization')).toBe('Bearer token-a');
    expect(secondInitial.request.headers.get('Authorization')).toBe('Bearer token-a');

    firstInitial.flush(null, { status: 401, statusText: 'Unauthorized' });
    await Promise.resolve();
    http.expectOne('/api/identity/v1/auth/refresh').flush(refreshedSession);
    let firstRetry: ReturnType<HttpTestingController['expectOne']> | undefined;
    await vi.waitFor(() => {
      firstRetry = http.expectOne('/api/first');
    });
    if (!firstRetry) throw new Error('The first request was not retried.');
    expect(firstRetry.request.headers.get('Authorization')).toBe('Bearer token-b');
    firstRetry.flush({ request: 'first' });
    await expect(first).resolves.toEqual({ request: 'first' });

    secondInitial.flush(null, { status: 401, statusText: 'Unauthorized' });
    let secondRetry: ReturnType<HttpTestingController['expectOne']> | undefined;
    await vi.waitFor(() => {
      http.expectNone('/api/identity/v1/auth/refresh');
      secondRetry = http.expectOne('/api/second');
    });
    if (!secondRetry) throw new Error('The staggered request was not retried.');
    expect(secondRetry.request.headers.get('Authorization')).toBe('Bearer token-b');
    secondRetry.flush({ request: 'second' });
    await expect(second).resolves.toEqual({ request: 'second' });
    http.verify();
  });

  it('does not refresh or redirect again for a staggered 401 after session clearing', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: RealtimeService,
          useValue: {
            reconnectWithFreshToken: () => Promise.resolve(null),
            disconnect: () => Promise.resolve(),
          },
        },
      ],
    });
    const client = TestBed.inject(HttpClient);
    const http = TestBed.inject(HttpTestingController);
    const tokens = TestBed.inject(AccessTokenStore);
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    tokens.set('token-a');

    const first = firstValueFrom(client.get('/api/first'));
    const second = firstValueFrom(client.get('/api/second'));
    const firstInitial = http.expectOne('/api/first');
    const secondInitial = http.expectOne('/api/second');
    firstInitial.flush(null, { status: 401, statusText: 'Unauthorized' });
    await Promise.resolve();
    http.expectOne('/api/identity/v1/auth/refresh').flush(null, {
      status: 401,
      statusText: 'Unauthorized',
    });
    await expect(first).rejects.toBeTruthy();
    expect(navigate).toHaveBeenCalledTimes(1);

    secondInitial.flush(null, { status: 401, statusText: 'Unauthorized' });
    await Promise.resolve();
    http.expectNone('/api/identity/v1/auth/refresh');
    await expect(second).rejects.toBeTruthy();
    expect(navigate).toHaveBeenCalledTimes(1);
    http.verify();
  });
});
