import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Auth, errorText } from '../core/auth';

@Component({
  selector: 'app-login',
  imports: [FormsModule],
  template: `
    <main>
      <form class="card" (ngSubmit)="submit()">
        <h1>Failure Triage</h1>
        <p class="muted">Sign in to triage automation failures into Jira bugs.</p>
        @if (error()) { <div class="alert bad" role="alert">{{ error() }}</div> }
        <label class="field">Email
          <input type="email" name="email" [(ngModel)]="email" autocomplete="username" required autofocus />
        </label>
        <label class="field">Password
          <input type="password" name="password" [(ngModel)]="password" autocomplete="current-password" required />
        </label>
        <button class="primary" type="submit" [disabled]="busy() || !email || !password">
          {{ busy() ? 'Signing in…' : 'Sign in' }}
        </button>
      </form>
    </main>`,
  styles: `
    main { min-height: 100vh; display: grid; place-items: center; padding: 16px; }
    form { width: min(380px, 100%); }
    button { width: 100%; justify-content: center; }
  `,
})
export class Login {
  private auth = inject(Auth);
  private router = inject(Router);
  email = '';
  password = '';
  busy = signal(false);
  error = signal('');

  async submit() {
    this.busy.set(true);
    this.error.set('');
    try {
      const u = await this.auth.login(this.email, this.password);
      await this.router.navigateByUrl(u.mustChangePassword ? '/profile' : '/');
    } catch (e) {
      this.error.set(errorText(e));
    } finally {
      this.busy.set(false);
    }
  }
}
