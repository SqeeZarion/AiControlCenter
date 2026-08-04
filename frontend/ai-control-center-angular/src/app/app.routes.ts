import { Routes } from '@angular/router';
import { authGuard, passwordChangedGuard } from './core/auth/auth.guard';
import { ChangePasswordComponent } from './features/auth/change-password/change-password.component';
import { LoginComponent } from './features/auth/login/login.component';
import { ShellComponent } from './layout/shell.component';
import { ForbiddenComponent } from './shared/pages/forbidden.component';

export const routes: Routes = [
  { path: 'login', component: LoginComponent },
  { path: 'change-password', component: ChangePasswordComponent, canActivate: [authGuard] },
  { path: 'forbidden', component: ForbiddenComponent, canActivate: [authGuard] },
  { path: '', component: ShellComponent, canActivate: [authGuard, passwordChangedGuard] },
  { path: '**', redirectTo: '' },
];
