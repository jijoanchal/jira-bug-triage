import { DecimalPipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { Api } from '../core/api';
import { Auth } from '../core/auth';
import { BudgetStatus } from '../core/models';

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, DecimalPipe],
  template: `
    <header class="no-print">
      <a class="brand" routerLink="/">Failure Triage</a>
      <nav>
        <a routerLink="/" routerLinkActive="on" [routerLinkActiveOptions]="{ exact: true }">Dashboard</a>
        <a routerLink="/runs" routerLinkActive="on">Runs</a>
        <a routerLink="/usage" routerLinkActive="on">AI usage</a>
        @if (auth.isAdmin()) { <a routerLink="/users" routerLinkActive="on">Users</a> }
      </nav>
      <span class="grow"></span>
      @if (budget(); as b) {
        @if (b.level !== 'none') {
          <a routerLink="/usage" class="badge budget" [class.ok]="b.level === 'ok'" [class.warn]="b.level === 'warning'" [class.bad]="b.level === 'over'"
             [title]="(b.limitSource === 'global' ? 'Organisation' : 'Your') + ' AI budget for ' + b.month + ': $' + b.spentUsd.toFixed(2) + ' of $' + b.limitUsd!.toFixed(2) + (b.enforced ? ' (enforced)' : ' (warning only)')">
            AI {{ b.percent! | number: '1.0-0' }}% of budget</a>
        }
      }
      <a routerLink="/profile" class="who">{{ auth.user()?.displayName }}</a>
      <button (click)="auth.logout()">Sign out</button>
    </header>
    <router-outlet />`,
  styles: `
    header { display: flex; align-items: center; gap: 18px; padding: 10px 24px; background: var(--surface); border-bottom: 1px solid var(--border); position: sticky; top: 0; z-index: 10; flex-wrap: wrap; }
    .brand { font-weight: 700; color: var(--text); font-size: 16px; }
    nav { display: flex; gap: 4px; }
    nav a { color: var(--muted); padding: 6px 10px; border-radius: 7px; }
    nav a.on { background: var(--info-bg); color: var(--primary); font-weight: 600; }
    nav a:hover { text-decoration: none; background: var(--surface-2); }
    .who { color: var(--muted); }
  `,
})
export class Shell implements OnInit {
  protected auth = inject(Auth);
  private api = inject(Api);
  private router = inject(Router);
  protected budget = signal<BudgetStatus | null>(null);

  ngOnInit() {
    this.refreshBudget();
    // Spend changes after AI actions, which end in a navigation; keep the chip current without polling.
    this.router.events.pipe(filter((e) => e instanceof NavigationEnd)).subscribe(() => this.refreshBudget());
  }

  private async refreshBudget() {
    try { this.budget.set(await this.api.myBudget()); } catch { /* the chip is optional */ }
  }
}
