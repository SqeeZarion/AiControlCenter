import { computed, inject, Injectable, signal } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { RealtimeService } from '../realtime/realtime.service';
import { AccessTokenStore } from './access-token.store';
import { AuthenticationState, AuthSessionDto, CurrentUserDto, LoginRequest } from './auth.models';
import { AuthService } from './auth.service';

@Injectable({ providedIn: 'root' })
export class AuthStore {
  private readonly auth = inject(AuthService);
  private readonly accessTokens = inject(AccessTokenStore);
  private readonly realtime = inject(RealtimeService);
  private readonly router = inject(Router);
  private readonly stateValue = signal<AuthenticationState>('unknown');
  private readonly userValue = signal<CurrentUserDto | null>(null);
  private refreshPromise?: Promise<boolean>;

  readonly state = this.stateValue.asReadonly();
  readonly user = this.userValue.asReadonly();
  readonly isAuthenticated = computed(() => this.stateValue() === 'authenticated');

  async initialize(): Promise<void> {
    if (this.stateValue() !== 'unknown') {
      return;
    }

    try {
      await firstValueFrom(this.auth.initializeCsrf());
      await this.refresh();
    } catch {
      await this.clearSession();
    }
  }

  async login(request: LoginRequest): Promise<CurrentUserDto> {
    const session = await firstValueFrom(this.auth.login(request));
    await this.applySession(session);
    return session.user;
  }

  refresh(): Promise<boolean> {
    if (this.refreshPromise) {
      return this.refreshPromise;
    }

    this.refreshPromise = this.withCrossTabRefreshLock(() => this.refreshCore())
      .finally(() => (this.refreshPromise = undefined));
    return this.refreshPromise;
  }

  async logout(): Promise<void> {
    try {
      await firstValueFrom(this.auth.logout());
    } finally {
      await this.clearSession();
      await this.router.navigate(['/login']);
    }
  }

  async changePassword(currentPassword: string, newPassword: string): Promise<void> {
    await firstValueFrom(this.auth.changePassword(currentPassword, newPassword));
    await this.clearSession();
    await this.router.navigate(['/login'], { queryParams: { passwordChanged: 'true' } });
  }

  hasAnyRole(roles: readonly string[]): boolean {
    const assigned = this.userValue()?.roles ?? [];
    return roles.some((role) => assigned.includes(role as never));
  }

  private async refreshCore(): Promise<boolean> {
    try {
      const session = await firstValueFrom(this.auth.refresh());
      await this.applySession(session);
      return true;
    } catch {
      await this.clearSession();
      return false;
    }
  }

  private async applySession(session: AuthSessionDto): Promise<void> {
    this.accessTokens.set(session.accessToken);
    this.userValue.set(session.user);
    this.stateValue.set('authenticated');
    await this.realtime.reconnectWithFreshToken();
  }

  private async clearSession(): Promise<void> {
    this.accessTokens.clear();
    this.userValue.set(null);
    this.stateValue.set('anonymous');
    await this.realtime.disconnect();
  }

  private withCrossTabRefreshLock(operation: () => Promise<boolean>): Promise<boolean> {
    if ('locks' in navigator) {
      return navigator.locks.request('aicontrolcenter-refresh', operation);
    }

    return operation();
  }
}
