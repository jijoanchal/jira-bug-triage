import { CurrencyPipe, DatePipe, DecimalPipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api } from '../core/api';
import { Auth, errorText } from '../core/auth';
import { UsagePolicy, UsageReport } from '../core/models';
import { BarRow, Series, StackedBars } from '../core/widgets';

/**
 * AI usage: tokens and estimated cost per month (UTC), by user, model, operation and day, the call-by-call log,
 * and (for admins) monthly budgets. Non-admins only ever see their own usage; the API enforces this.
 */
@Component({
  selector: 'app-usage',
  imports: [FormsModule, DecimalPipe, CurrencyPipe, DatePipe, RouterLink, StackedBars],
  template: `
    <div class="page">
      <div class="page-head">
        <div><h1>AI usage</h1>
          <p class="muted">Token consumption and estimated cost of every Anthropic call made for triage, re-triage and merge. Months are UTC.</p></div>
        <div class="filters">
          <label class="field">Month
            <select [(ngModel)]="month" (ngModelChange)="load()" name="month">
              @for (m of months(); track m) { <option [value]="m">{{ m }}</option> }
            </select>
          </label>
          @if (auth.isAdmin()) {
            <label class="field">User
              <select [(ngModel)]="user" (ngModelChange)="load()" name="user">
                <option value="">All users</option>
                @for (u of report()?.userBudgets ?? []; track u.userId) { <option [value]="u.userId">{{ u.displayName }}</option> }
              </select>
            </label>
          }
          <button (click)="load()" [disabled]="loading()">Refresh</button>
          <button (click)="exportCsv()" [disabled]="!report()?.records?.length">Export CSV</button>
        </div>
      </div>

      @if (error()) { <div class="alert bad" role="alert">{{ error() }}</div> }

      @if (report(); as r) {
        @if (r.totals.unpricedCalls > 0) {
          <div class="alert warn">{{ r.totals.unpricedCalls }} call(s) used a model with no price configured, so their cost shows as $0.
            Add the model under <code>Ai:Models</code> in appsettings.json.</div>
        }
        @if (r.totals.blocked > 0) {
          <div class="alert info">{{ r.totals.blocked }} call(s) were refused because a monthly budget had been reached.</div>
        }

        <div class="grid kpis">
          <div class="kpi"><div class="v">{{ r.totals.calls | number }}</div>
            <div class="l">AI calls · {{ r.totals.failed | number }} failed · {{ r.totals.retries | number }} retries</div></div>
          <div class="kpi"><div class="v">{{ compact(r.totals.totalTokens) }}</div>
            <div class="l">Tokens · {{ compact(r.totals.inputTokens + r.totals.cacheWriteTokens + r.totals.cacheReadTokens) }} in · {{ compact(r.totals.outputTokens) }} out</div></div>
          <div class="kpi"><div class="v">{{ r.totals.costUsd | currency: 'USD' : 'symbol' : (r.totals.costUsd < 1 ? '1.4-4' : '1.2-2') }}</div>
            <div class="l">Estimated cost</div></div>
          <div class="kpi"><div class="v">{{ (r.totals.calls ? r.totals.costUsd / r.totals.calls : 0) | currency: 'USD' : 'symbol' : '1.4-4' }}</div>
            <div class="l">Cost per call</div></div>
          <div class="kpi"><div class="v">{{ r.totals.avgDurationMs / 1000 | number: '1.1-1' }} s</div>
            <div class="l">Average call time</div></div>
        </div>

        @if (!r.records.length) {
          <section class="card"><p class="muted">No AI calls were made in {{ r.month }}{{ r.user ? ' by this user' : '' }}.</p></section>
        } @else {
          <div class="grid two">
            <section class="card"><h2>Daily cost (USD)</h2>
              <app-stacked-bars [rows]="dayRows()" [series]="daySeries" label="Daily AI cost in USD" /></section>
            <section class="card"><h2>By operation</h2>
              <div class="scroll"><table>
                <thead><tr><th>Operation</th><th class="num">Calls</th><th class="num">Failed</th><th class="num">Tokens</th><th class="num">Cost</th></tr></thead>
                <tbody>@for (g of r.byOperation; track g.key) {
                  <tr><td>{{ g.key }}</td><td class="num">{{ g.calls | number }}</td><td class="num">{{ g.failed | number }}</td>
                    <td class="num">{{ g.totalTokens | number }}</td><td class="num">{{ g.costUsd | currency: 'USD' : 'symbol' : '1.4-4' }}</td></tr>
                }</tbody>
              </table></div>
              <h2 class="gap">By model</h2>
              <div class="scroll"><table>
                <thead><tr><th>Model</th><th class="num">Calls</th><th class="num">Input</th><th class="num">Output</th><th class="num">Cost</th></tr></thead>
                <tbody>@for (g of r.byModel; track g.key) {
                  <tr><td>{{ g.key }} @if (!g.priced) { <span class="badge warn" title="No price configured">unpriced</span> }</td>
                    <td class="num">{{ g.calls | number }}</td><td class="num">{{ g.inputTokens | number }}</td><td class="num">{{ g.outputTokens | number }}</td>
                    <td class="num">{{ g.costUsd | currency: 'USD' : 'symbol' : '1.4-4' }}</td></tr>
                }</tbody>
              </table></div>
            </section>
          </div>

          @if (auth.isAdmin()) {
            <section class="card"><h2>By user</h2>
              <div class="scroll"><table>
                <thead><tr><th>User</th><th class="num">Calls</th><th class="num">Failed</th><th class="num">Blocked</th><th class="num">Tokens</th>
                  <th class="num">Cost</th><th class="num">This month vs budget</th><th>Last call</th></tr></thead>
                <tbody>@for (u of r.byUser; track u.userName) {
                  <tr><td>{{ u.displayName ? u.displayName + ' (' + u.userName + ')' : u.userName }}</td>
                    <td class="num">{{ u.calls | number }}</td><td class="num">{{ u.failed | number }}</td><td class="num">{{ u.blocked | number }}</td>
                    <td class="num">{{ u.totalTokens | number }}</td><td class="num">{{ u.costUsd | currency: 'USD' : 'symbol' : '1.4-4' }}</td>
                    <td class="num">
                      @if (u.percent != null) {
                        <span class="badge" [class.ok]="u.percent < pol()!.warningPercent" [class.warn]="u.percent >= pol()!.warningPercent && u.percent < 100" [class.bad]="u.percent >= 100">
                          {{ u.percent | number: '1.0-0' }}% of {{ u.monthlyLimitUsd | currency: 'USD' }}</span>
                      } @else { <span class="muted">no limit</span> }
                    </td>
                    <td class="muted">{{ u.lastUsedUtc | date: 'medium' }}</td></tr>
                }</tbody>
              </table></div>
            </section>
          }

          <section class="card"><h2>Calls ({{ r.records.length | number }}{{ r.recordsTruncated ? ', latest only — export is also limited to these' : '' }})</h2>
            <div class="scroll"><table>
              <thead><tr><th>When</th>@if (!r.user) { <th>User</th> }<th>Operation</th><th>Model</th><th>Status</th>
                <th class="num">Input</th><th class="num">Cache</th><th class="num">Output</th><th class="num">Cost</th><th class="num">Time</th><th>Source</th></tr></thead>
              <tbody>@for (q of r.records; track q.id) {
                <tr>
                  <td class="muted">{{ q.timestampUtc | date: 'MMM d, HH:mm:ss' }}</td>
                  @if (!r.user) { <td>{{ q.userName }}</td> }
                  <td>{{ q.operation }}</td>
                  <td class="muted">{{ q.model }}</td>
                  <td><span class="badge" [class.ok]="q.status === 'ok'" [class.bad]="q.status === 'error' || q.status === 'bad-output'" [class.warn]="q.status === 'blocked'"
                        [title]="q.error ?? ''">{{ q.status }}</span>@if (q.attempts > 1) { <span class="muted small"> ×{{ q.attempts }}</span> }</td>
                  <td class="num">{{ q.inputTokens | number }}</td>
                  <td class="num" [title]="'write ' + q.cacheWriteTokens + ' / read ' + q.cacheReadTokens">{{ (q.cacheWriteTokens + q.cacheReadTokens) | number }}</td>
                  <td class="num">{{ q.outputTokens | number }}</td>
                  <td class="num">{{ q.costUsd | currency: 'USD' : 'symbol' : '1.4-4' }}</td>
                  <td class="num">{{ q.durationMs / 1000 | number: '1.1-1' }} s</td>
                  <td>@if (q.runId) { <a [routerLink]="['/runs', q.runId]">run {{ q.runId }}</a> }</td>
                </tr>
              }</tbody>
            </table></div>
          </section>
        }

        @if (auth.isAdmin() && pol(); as p) {
          <section class="card"><h2>Governance</h2>
            <p class="muted small">
              Limits are monthly (UTC). {{ p.enforced ? 'Enforcement is ON: AI calls are refused once a limit is reached.' :
                'Enforcement is OFF: limits only show a warning at ' + p.warningPercent + '% and when exceeded. Set Ai:EnforceBudget to true to block calls.' }}
            </p>
            @if (budgetMsg()) { <div class="alert ok" role="status">{{ budgetMsg() }}</div> }
            <div class="scroll"><table>
              <thead><tr><th>Scope</th><th class="num">Spent this month</th><th class="num">Monthly limit (USD)</th><th></th></tr></thead>
              <tbody>
                <tr>
                  <td><strong>Everyone (global)</strong>
                    @if (p.globalLimitSource === 'config') { <span class="muted small"> · from configuration</span> }</td>
                  <td class="num">{{ totalSpentNow() | currency: 'USD' : 'symbol' : '1.2-4' }}</td>
                  <td class="num"><input type="number" min="0" step="1" class="limit" [ngModel]="edits()['global'] ?? p.globalLimitUsd" (ngModelChange)="edit('global', $event)" aria-label="Global monthly limit" /></td>
                  <td><button class="primary" (click)="saveBudget('global')" [disabled]="!dirty('global')">Save</button></td>
                </tr>
                @for (u of r.userBudgets ?? []; track u.userId) {
                  <tr>
                    <td>{{ u.displayName }} <span class="muted small">{{ u.email }}</span></td>
                    <td class="num">{{ u.spentUsd | currency: 'USD' : 'symbol' : '1.2-4' }}</td>
                    <td class="num"><input type="number" min="0" step="1" class="limit" [ngModel]="edits()[u.userId] ?? u.limitUsd" (ngModelChange)="edit(u.userId, $event)" [attr.aria-label]="'Monthly limit for ' + u.displayName" /></td>
                    <td><button class="primary" (click)="saveBudget(u.userId)" [disabled]="!dirty(u.userId)">Save</button></td>
                  </tr>
                }
              </tbody>
            </table></div>
            <p class="muted small">Leave a limit empty (or 0) for no limit. Prices used for cost estimates (USD per million tokens):
              @for (m of p.models; track m.id) { <span class="price">{{ m.label || m.id }}: {{ m.inputPerMTok }} in / {{ m.outputPerMTok }} out / {{ m.cacheReadPerMTok }} cache read</span> }
            </p>
          </section>
        }
      }
    </div>`,
  styles: `
    .filters { display: flex; gap: 12px; align-items: flex-end; flex-wrap: wrap; }
    .filters .field { margin-bottom: 0; }
    .two { grid-template-columns: repeat(auto-fit, minmax(340px, 1fr)); margin-bottom: 16px; }
    .scroll { overflow-x: auto; }
    .gap { margin-top: 18px; }
    input.limit { width: 110px; text-align: right; }
    .price { display: inline-block; margin-right: 14px; }
    section.card { margin-bottom: 16px; }
  `,
})
export class UsagePage implements OnInit {
  private api = inject(Api);
  protected auth = inject(Auth);

  months = signal<string[]>([]);
  month = '';
  user = '';
  report = signal<UsageReport | null>(null);
  pol = signal<UsagePolicy | null>(null);
  loading = signal(false);
  error = signal('');
  budgetMsg = signal('');
  edits = signal<Record<string, number | null | undefined>>({});

  readonly daySeries: Series[] = [{ label: 'Cost (USD)', color: '#5b84d6' }];
  dayRows = computed<BarRow[]>(() => (this.report()?.byDay ?? []).map((d) => ({
    label: d.day.slice(8), title: `${d.day}: $${d.costUsd.toFixed(4)}, ${d.calls} call(s), ${d.totalTokens.toLocaleString()} tokens`, values: [d.costUsd],
  })));
  totalSpentNow = computed(() => (this.report()?.userBudgets ?? []).reduce((s, u) => s + u.spentUsd, 0));

  async ngOnInit() {
    try {
      this.months.set(await this.api.usageMonths());
      this.month = this.months()[0];
      if (this.auth.isAdmin()) this.pol.set(await this.api.usagePolicy());
    } catch (e) { this.error.set(errorText(e)); return; }
    await this.load();
  }

  async load() {
    this.loading.set(true); this.error.set('');
    try { this.report.set(await this.api.usageReport(this.month, this.user || undefined)); }
    catch (e) { this.error.set(errorText(e)); }
    finally { this.loading.set(false); }
  }

  compact(n: number): string {
    return n >= 1e6 ? (n / 1e6).toFixed(2) + 'M' : n >= 1e3 ? (n / 1e3).toFixed(1) + 'k' : String(n);
  }

  private current(scope: string): number | null {
    if (scope === 'global') return this.pol()?.globalLimitUsd ?? null;
    return this.report()?.userBudgets?.find((u) => u.userId === scope)?.limitUsd ?? null;
  }
  edit(scope: string, v: number | null) { this.edits.update((e) => ({ ...e, [scope]: v })); }
  dirty(scope: string): boolean {
    const e = this.edits()[scope];
    return e !== undefined && (e || null) !== this.current(scope);
  }

  async saveBudget(scope: string) {
    this.error.set(''); this.budgetMsg.set('');
    try {
      await this.api.setBudget(scope, this.edits()[scope] || null);
      this.edits.update((e) => { const { [scope]: _, ...rest } = e; return rest; });
      this.pol.set(await this.api.usagePolicy());
      await this.load();
      this.budgetMsg.set('Budget saved.');
    } catch (e) { this.error.set(errorText(e)); }
  }

  exportCsv() {
    const r = this.report();
    if (!r) return;
    const q = (v: unknown) => `"${String(v ?? '').replace(/"/g, '""')}"`;
    const head = ['When (UTC)', 'User', 'Operation', 'Run', 'Bug', 'Model', 'Status', 'Attempts', 'Input tokens', 'Cache write tokens',
      'Cache read tokens', 'Output tokens', 'Total tokens', 'Cost USD', 'Priced', 'Duration ms', 'Request id', 'Error'];
    const rows = r.records.map((x) => [x.timestampUtc, x.userName, x.operation, x.runId, x.bugId, x.model, x.status, x.attempts,
      x.inputTokens, x.cacheWriteTokens, x.cacheReadTokens, x.outputTokens, x.totalTokens, x.costUsd, x.priced, x.durationMs, x.requestId, x.error]);
    const csv = [head, ...rows].map((l) => l.map(q).join(',')).join('\r\n');
    const url = URL.createObjectURL(new Blob(['﻿' + csv], { type: 'text/csv' }));
    const a = document.createElement('a');
    a.href = url; a.download = `ai-usage-${r.month}.csv`; a.click();
    URL.revokeObjectURL(url);
  }
}
