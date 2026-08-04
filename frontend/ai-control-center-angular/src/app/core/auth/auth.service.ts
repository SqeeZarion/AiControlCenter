import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { AuthSessionDto, LoginRequest } from './auth.models';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/identity/v1';

  initializeCsrf(): Observable<void> {
    return this.http.get<void>(`${this.baseUrl}/auth/csrf`, { withCredentials: true });
  }

  login(request: LoginRequest): Observable<AuthSessionDto> {
    return this.http.post<AuthSessionDto>(`${this.baseUrl}/auth/login`, request, {
      withCredentials: true,
    });
  }

  refresh(): Observable<AuthSessionDto> {
    return this.http.post<AuthSessionDto>(`${this.baseUrl}/auth/refresh`, null, {
      withCredentials: true,
    });
  }

  logout(): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/auth/logout`, null, { withCredentials: true });
  }

  logoutAll(): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/auth/logout-all`, null, { withCredentials: true });
  }

  changePassword(currentPassword: string, newPassword: string): Observable<void> {
    return this.http.post<void>(
      `${this.baseUrl}/users/me/change-password`,
      { currentPassword, newPassword },
      { withCredentials: true },
    );
  }
}
