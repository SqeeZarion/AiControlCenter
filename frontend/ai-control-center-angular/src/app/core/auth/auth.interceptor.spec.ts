import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { AccessTokenStore } from './access-token.store';
import { AuthStore } from './auth.store';
import { authInterceptor } from './auth.interceptor';
import { HttpClient } from '@angular/common/http';

describe('authInterceptor', () => {
  let http: HttpTestingController;
  let client: HttpClient;
  let tokens: AccessTokenStore;
  let refreshCalls: number;

  beforeEach(() => {
    refreshCalls = 0;
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        AccessTokenStore,
        {
          provide: AuthStore,
          useValue: {
            refresh: async () => {
              refreshCalls++;
              TestBed.inject(AccessTokenStore).set('new-token');
              return true;
            },
          },
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    client = TestBed.inject(HttpClient);
    tokens = TestBed.inject(AccessTokenStore);
    tokens.set('old-token');
  });

  afterEach(() => http.verify());

  it('adds Bearer and retries a 401 only once after refresh', async () => {
    const responsePromise = firstValueFrom(client.get<{ ok: boolean }>('/api/protected'));
    const initial = http.expectOne('/api/protected');
    expect(initial.request.headers.get('Authorization')).toBe('Bearer old-token');
    initial.flush(null, { status: 401, statusText: 'Unauthorized' });
    await Promise.resolve();

    const retry = http.expectOne('/api/protected');
    expect(retry.request.headers.get('Authorization')).toBe('Bearer new-token');
    retry.flush({ ok: true });

    expect(await responsePromise).toEqual({ ok: true });
    expect(refreshCalls).toBe(1);
  });

  it('does not enter a refresh loop when the retried request is rejected', async () => {
    const responsePromise = firstValueFrom(client.get('/api/protected'));
    http.expectOne('/api/protected').flush(null, { status: 401, statusText: 'Unauthorized' });
    await Promise.resolve();
    http.expectOne('/api/protected').flush(null, { status: 401, statusText: 'Unauthorized' });

    await expect(responsePromise).rejects.toBeTruthy();
    expect(refreshCalls).toBe(1);
  });

  it('never sends the platform token to a cross-origin URL', () => {
    client.get('https://example.test/data').subscribe();
    const request = http.expectOne('https://example.test/data');
    expect(request.request.headers.has('Authorization')).toBe(false);
    request.flush({});
  });

  it.each([
    '//attacker.example/path',
    'javascript:alert(1)',
    'data:text/plain,token',
    'http://[::1',
    'http://attacker.example/path',
  ])('never sends the platform token to unsafe URL %s', (url) => {
    client.get(url).subscribe();
    const request = http.expectOne(url);
    expect(request.request.headers.has('Authorization')).toBe(false);
    request.flush({});
  });

  it('fails closed without globalThis.location', () => {
    const originalLocation = globalThis.location;
    vi.stubGlobal('location', undefined);
    try {
      client.get('/api/protected').subscribe();
      const request = http.expectOne('/api/protected');
      expect(request.request.headers.has('Authorization')).toBe(false);
      request.flush({});
    } finally {
      vi.stubGlobal('location', originalLocation);
    }
  });

  it.each([
    '/api/resource',
    'api/resource',
    `${globalThis.location.origin}/api/resource`,
  ])('sends the platform token to same-origin URL %s', (url) => {
    client.get(url).subscribe();
    const request = http.expectOne(url);
    expect(request.request.headers.get('Authorization')).toBe('Bearer old-token');
    request.flush({});
  });

  it('does not add Bearer to an absolute same-origin public auth URL', () => {
    const url = `${globalThis.location.origin}/api/identity/v1/auth/refresh`;
    client.post(url, {}).subscribe();
    const request = http.expectOne(url);
    expect(request.request.headers.has('Authorization')).toBe(false);
    request.flush({});
  });
});
