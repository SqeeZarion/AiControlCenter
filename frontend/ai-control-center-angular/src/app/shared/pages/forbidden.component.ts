import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-forbidden',
  imports: [RouterLink],
  template: '<main><h1>403</h1><p>Недостатньо прав для цієї сторінки.</p><a routerLink="/">Повернутися</a></main>',
  styles: [':host,main{display:grid;place-items:center;align-content:center;min-height:100vh}h1{font-size:5rem;margin:0}'],
})
export class ForbiddenComponent {}
