import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Auth, errorText } from '../core/auth';

@Component({
  selector: 'app-profile',
  imports: [FormsModule],
  template: `
    <div class="page narrow">
      <h1>My account</h1>
      <p class="muted">{{ auth.user()?.displayName }} · {{ auth.user()?.email }} · {{ auth.user()?.role }}</p>
      @if (auth.user()?.mustChangePassword) { <div class="alert warn">You must set a new password before continuing.</div> }
      @if (ok()) { <div class="alert ok" role="status">Password changed.</div> }
      @if (error()) { <div class="alert bad" role="alert">{{ error() }}</div> }
      <form class="card" (ngSubmit)="submit()">
        <h2>Change password</h2>
        <label class="field">Current password<input type="password" name="cur" [(ngModel)]="current" autocomplete="current-password" required /></label>
        <label class="field">New password (8+ characters, letters and digits)<input type="password" name="new" [(ngModel)]="next" autocomplete="new-password" required /></label>
        <label class="field">Confirm new password<input type="password" name="conf" [(ngModel)]="confirm" autocomplete="new-password" required /></label>
        <button class="primary" type="submit" [disabled]="busy() || !current || !next || next !== confirm">Change password</button>
        @if (confirm && next !== confirm) { <span class="muted small"> Passwords do not match.</span> }
      </form>
    </div>`,
  styles: `.narrow { max-width: 520px; }`,
})
export class ProfilePage {
  protected auth = inject(Auth);
  private router = inject(Router);
  current = ''; next = ''; confirm = '';
  busy = signal(false); ok = signal(false); error = signal('');

  async submit() {
    this.busy.set(true); this.error.set(''); this.ok.set(false);
    try {
      await this.auth.changePassword(this.current, this.next);
      this.current = this.next = this.confirm = '';
      this.ok.set(true);
      await this.router.navigateByUrl('/profile');
    } catch (e) { this.error.set(errorText(e)); } finally { this.busy.set(false); }
  }
}
