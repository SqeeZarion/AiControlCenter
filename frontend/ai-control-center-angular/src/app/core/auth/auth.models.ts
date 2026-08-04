export type PlatformRole = 'Admin' | 'Developer' | 'User';

export interface CurrentUserDto {
  id: string;
  email: string;
  displayName: string;
  roles: PlatformRole[];
  mustChangePassword: boolean;
}

export interface AuthSessionDto {
  accessToken: string;
  tokenType: 'Bearer';
  expiresInSeconds: number;
  user: CurrentUserDto;
}

export interface LoginRequest {
  email: string;
  password: string;
}

export type AuthenticationState = 'unknown' | 'anonymous' | 'authenticated';
