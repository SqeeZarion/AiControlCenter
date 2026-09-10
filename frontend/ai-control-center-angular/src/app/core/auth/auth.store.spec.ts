import { provideHttpClient, withXsrfConfiguration } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RealtimeService } from '../realtime/realtime.service';
import { AccessTokenStore } from './access-token.store';
import { AuthSessionDto } from './auth.models';
import { AuthStore } from './auth.store';

describe('AuthStore', () => {
  const session: AuthSessionDto = {
    accessToken: 'access-token',
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

  let store: AuthStore;
  let http: HttpTestingController;
  let accessTokens: AccessTokenStore;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(
          withXsrfConfiguration({ cookieName: 'XSRF-TOKEN', headerName: 'X-XSRF-TOKEN' }),
        ),
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
    store = TestBed.inject(AuthStore);
    http = TestBed.inject(HttpTestingController);
    accessTokens = TestBed.inject(AccessTokenStore);
  });

  afterEach(() => http.verify());

  it('bootstraps CSRF then restores a session without browser storage', async () => {
    const initialized = store.initialize();
    http.expectOne('/api/identity/v1/auth/csrf').flush(null);
    await Promise.resolve();
    http.expectOne('/api/identity/v1/auth/refresh').flush(session);

    await initialized;

    expect(store.state()).toBe('authenticated');
    expect(store.user()?.email).toBe(session.user.email);
    expect(accessTokens.token()).toBe('access-token');
    expect(localStorage.getItem('accessToken')).toBeNull();
    expect(sessionStorage.getItem('accessToken')).toBeNull();
  });

  it('uses one in-flight refresh for concurrent callers', async () => {
    const first = store.refresh();
    const second = store.refresh();
    const request = http.expectOne('/api/identity/v1/auth/refresh');
    request.flush(session);

    expect(await first).toBe(true);
    expect(await second).toBe(true);
  });

  it('clears the session and navigates once when a shared refresh fails', async () => {
    accessTokens.set('expired-token');
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);

    const first = store.refresh();
    const second = store.refresh();
    http.expectOne('/api/identity/v1/auth/refresh').flush(null, {
      status: 401,
      statusText: 'Unauthorized',
    });

    expect(await first).toBe(false);
    expect(await second).toBe(false);
    expect(accessTokens.token()).toBeNull();
    expect(store.state()).toBe('anonymous');
    expect(navigate).toHaveBeenCalledTimes(1);
    expect(navigate).toHaveBeenCalledWith(['/login'], expect.anything());
  });
});
