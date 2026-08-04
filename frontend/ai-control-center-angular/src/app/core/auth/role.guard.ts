import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthStore } from './auth.store';

export const roleGuard: CanActivateFn = (route) => {
  const requiredRoles = (route.data['roles'] as readonly string[] | undefined) ?? [];
  return requiredRoles.length === 0 || inject(AuthStore).hasAnyRole(requiredRoles)
    ? true
    : inject(Router).createUrlTree(['/forbidden']);
};
