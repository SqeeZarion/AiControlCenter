import { Component, input } from '@angular/core';

@Component({
  selector: 'app-connection-status',
  template: `
    <span class="status" [class.online]="connected()">
      <span class="dot" aria-hidden="true"></span>
      {{ connected() ? 'Connected' : 'Unavailable' }}
    </span>
  `,
  styles: `
    .status { display: inline-flex; align-items: center; gap: .5rem; color: #a9b4c8; }
    .dot { width: .65rem; height: .65rem; border-radius: 50%; background: #f87171; }
    .online .dot { background: #34d399; box-shadow: 0 0 0 .25rem rgb(52 211 153 / 12%); }
  `,
})
export class ConnectionStatusComponent {
  readonly connected = input.required<boolean>();
}
