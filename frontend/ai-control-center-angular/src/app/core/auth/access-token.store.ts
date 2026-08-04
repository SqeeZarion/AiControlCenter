import { Injectable, signal } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class AccessTokenStore {
  private readonly value = signal<string | null>(null);

  readonly token = this.value.asReadonly();

  set(token: string): void {
    this.value.set(token);
  }

  clear(): void {
    this.value.set(null);
  }
}
