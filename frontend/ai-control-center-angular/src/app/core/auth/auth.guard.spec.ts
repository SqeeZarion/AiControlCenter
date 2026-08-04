import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, provideRouter, Router, RouterStateSnapshot, UrlTree } from '@angular/router';
import { AuthStore } from './auth.store';
import { authGuard } from './auth.guard';
import { roleGuard } from './role.guard';

describe('authentication guards', () => {
  let authenticated: boolean;
  let roles: string[];

  beforeEach(() => {
    authenticated = false;
    roles = [];
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: AuthStore,
          useValue: {
            isAuthenticated: () => authenticated,
            hasAnyRole: (required: readonly string[]) => required.some((role) => roles.includes(role)),
          },
        },
      ],
    });
  });

  it('returns a login UrlTree with returnUrl for an anonymous user', () => {
    const result = TestBed.runInInjectionContext(() =>
      authGuard({} as ActivatedRouteSnapshot, { url: '/admin' } as RouterStateSnapshot));

    expect(result instanceof UrlTree).toBe(true);
    expect(TestBed.inject(Router).serializeUrl(result as UrlTree)).toContain('returnUrl=%2Fadmin');
  });

  it('uses CurrentUser roles for role authorization', () => {
    roles = ['Developer'];
    const route = { data: { roles: ['Admin'] } } as unknown as ActivatedRouteSnapshot;
    const result = TestBed.runInInjectionContext(() =>
      roleGuard(route, { url: '/admin' } as RouterStateSnapshot));

    expect(result instanceof UrlTree).toBe(true);
    expect(TestBed.inject(Router).serializeUrl(result as UrlTree)).toBe('/forbidden');
  });
});
