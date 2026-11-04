import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom, tap } from 'rxjs';
import { catchError, throwError } from 'rxjs';
import { User } from './models';

const TOKEN_KEY = 'triage.token';

@Injectable({ providedIn: 'root' })
export class Auth {
  private http = inject(HttpClient);
  private router = inject(Router);

  readonly user = signal<User | null>(null);
  readonly isAdmin = computed(() => this.user()?.role === 'Admin');

  get token(): string | null {
    try { return localStorage.getItem(TOKEN_KEY); } catch { return null; }
  }

  /** Restores the session on page load. */
  async restore(): Promise<void> {
    if (!this.token || this.user()) return;
    try {
      this.user.set(await firstValueFrom(this.http.get<User>('/api/auth/me')));
    } catch {
      this.clear();
    }
  }

  async login(email: string, password: string): Promise<User> {
    const res = await firstValueFrom(
      this.http.post<{ token: string; user: User }>('/api/auth/login', { email, password }),
    );
    localStorage.setItem(TOKEN_KEY, res.token);
    this.user.set(res.user);
    return res.user;
  }

  async changePassword(currentPassword: string, newPassword: string): Promise<void> {
    const u = await firstValueFrom(
      this.http.post<User>('/api/auth/change-password', { currentPassword, newPassword }),
    );
    this.user.set(u);
  }

  logout(): void {
    this.clear();
    this.router.navigateByUrl('/login');
  }

  private clear(): void {
    try { localStorage.removeItem(TOKEN_KEY); } catch { /* ignore */ }
    this.user.set(null);
  }
}

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(Auth);
  const token = auth.token;
  const authed = token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req;
  return next(authed).pipe(
    catchError((e: HttpErrorResponse) => {
      if (e.status === 401 && !req.url.endsWith('/auth/login')) auth.logout();
      return throwError(() => e);
    }),
  );
};

export const authGuard: CanActivateFn = async (_route, state) => {
  const auth = inject(Auth);
  const router = inject(Router);
  await auth.restore();
  const u = auth.user();
  if (!u) return router.createUrlTree(['/login']);
  if (u.mustChangePassword && state.url !== '/profile') return router.createUrlTree(['/profile']);
  return true;
};

export const adminGuard: CanActivateFn = () => {
  const auth = inject(Auth);
  return auth.isAdmin() || inject(Router).createUrlTree(['/']);
};

/** Extracts a readable message from an API error. */
export function errorText(e: unknown): string {
  if (e instanceof HttpErrorResponse) {
    if (e.status === 0) return 'Cannot reach the server.';
    const m = (e.error as { message?: string } | null)?.message;
    return m ?? `Request failed (${e.status}).`;
  }
  return e instanceof Error ? e.message : 'Something went wrong.';
}
