import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ProviderApiService } from './provider-api.service';

describe('ProviderApiService', () => {
  let api: ProviderApiService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(ProviderApiService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('sends a write-only credential only in create and replace request bodies', () => {
    api.create('OpenAi', 'Personal OpenAI', 'temporary-secret').subscribe();
    const create = http.expectOne('/api/integrations/v1/provider-connections');
    expect(create.request.method).toBe('POST');
    expect(create.request.body).toEqual({
      provider: 'OpenAi',
      displayName: 'Personal OpenAI',
      apiKey: 'temporary-secret',
    });
    create.flush({});

    api.replaceCredential('connection/1', 'replacement-secret').subscribe();
    const replace = http.expectOne(
      '/api/integrations/v1/provider-connections/connection%2F1/credential',
    );
    expect(replace.request.method).toBe('PUT');
    expect(replace.request.body).toEqual({ apiKey: 'replacement-secret' });
    replace.flush({});
  });

  it('uses owner-scoped model and usage endpoints', () => {
    api.models('connection-1').subscribe();
    http.expectOne('/api/integrations/v1/provider-connections/connection-1/models').flush([]);

    api.usage(7).subscribe();
    const usage = http.expectOne(
      (request) =>
        request.url === '/api/integrations/v1/provider-connections/usage' &&
        request.params.get('days') === '7',
    );
    expect(usage.request.method).toBe('GET');
    usage.flush({});
  });

  it('queries OAuth configuration and starts only the server-configured callback flow', () => {
    api.githubOAuthStatus().subscribe();
    const status = http.expectOne('/api/integrations/v1/provider-connections/github/oauth/status');
    expect(status.request.method).toBe('GET');
    status.flush({ status: 'NotConfigured' });

    api.githubOAuthStart().subscribe();
    const start = http.expectOne('/api/integrations/v1/provider-connections/github/oauth/start');
    expect(start.request.method).toBe('POST');
    expect(start.request.body).toEqual({});
    start.flush({ status: 'Available', authorizationUri: 'https://github.com/' });
  });

  it('sends a GitHub fine-grained PAT only in the write-only request body', () => {
    api.connectGitHubPat('github_pat_write-only-secret', 'expected-user').subscribe();
    const request = http.expectOne('/api/integrations/v1/provider-connections/github/pat');

    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({
      personalAccessToken: 'github_pat_write-only-secret',
      expectedGitHubLogin: 'expected-user',
    });
    request.flush({ credentialFingerprint: 'sha256:masked' });
  });

  it('uses separate endpoints for validation and confirmed Local CLI reauthentication', () => {
    api.startLocalCopilotReconnect().subscribe();
    const validate = http.expectOne(
      '/api/integrations/v1/provider-connections/local-copilot/reconnect',
    );
    expect(validate.request.method).toBe('POST');
    expect(validate.request.body).toEqual({});
    validate.flush({ state: 'Authorized' });

    api.startLocalCopilotReauthentication().subscribe();
    const reauthenticate = http.expectOne(
      '/api/integrations/v1/provider-connections/local-copilot/reauthenticate',
    );
    expect(reauthenticate.request.method).toBe('POST');
    expect(reauthenticate.request.body).toEqual({ confirmed: true });
    reauthenticate.flush({ state: 'WaitingForUser' });

    api.currentLocalCopilotReconnect().subscribe();
    const current = http.expectOne(
      '/api/integrations/v1/provider-connections/local-copilot/reconnect/current',
    );
    expect(current.request.method).toBe('GET');
    current.flush(null);

    api.logoutLocalCopilotSession().subscribe();
    const logout = http.expectOne('/api/integrations/v1/provider-connections/local-copilot/logout');
    expect(logout.request.method).toBe('POST');
    expect(logout.request.body).toEqual({ confirmed: true });
    logout.flush({ status: 'Succeeded', errorCode: null });
  });
});
