import { Component, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { AuthStore } from '../../../core/auth/auth.store';

@Component({
  selector: 'app-change-password',
  imports: [ReactiveFormsModule],
  template: `
    <main class="page">
      <form [formGroup]="form" (ngSubmit)="submit()">
        <h1>Змініть тимчасовий пароль</h1>
        <p>Після зміни потрібно увійти ще раз.</p>
        @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
        <label>Поточний пароль <input type="password" formControlName="currentPassword" /></label>
        <label>Новий пароль <input type="password" formControlName="newPassword" /></label>
        <button type="submit" [disabled]="submitting()">Змінити пароль</button>
      </form>
    </main>
  `,
  styles: [`
    :host,.page{display:grid;min-height:100vh;place-items:center}.page{padding:1.5rem}
    form{display:grid;gap:1rem;width:min(440px,100%);padding:2rem;border:1px solid #293449;border-radius:1rem;background:#0d1525}
    h1,p{margin:0}label{display:grid;gap:.4rem}input,button{padding:.8rem;border-radius:.6rem;font:inherit}
    input{border:1px solid #334155;color:#fff;background:#080f1d}button{border:0;background:#7dd3fc;font-weight:800}.error{color:#fecaca}
  `],
})
export class ChangePasswordComponent {
  private readonly auth = inject(AuthStore);
  readonly error = signal<string | null>(null);
  readonly submitting = signal(false);
  readonly form = new FormGroup({
    currentPassword: new FormControl('', { nonNullable: true, validators: Validators.required }),
    newPassword: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.minLength(12)] }),
  });

  async submit(): Promise<void> {
    if (this.form.invalid) return;
    this.submitting.set(true);
    this.error.set(null);
    try {
      const value = this.form.getRawValue();
      await this.auth.changePassword(value.currentPassword, value.newPassword);
    } catch {
      this.error.set('Не вдалося змінити пароль.');
      this.submitting.set(false);
    }
  }
}
