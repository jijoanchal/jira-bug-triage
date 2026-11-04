import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api } from '../core/api';
import { errorText } from '../core/auth';
import { Dashboard, SystemConfig } from '../core/models';
import { BarRow, Series, StackedBars } from '../core/widgets';

@Component({
  selector: 'app-dashboard',
  imports: [RouterLink, DatePipe, StackedBars],
  template: `
    <div class="page">
      <div class="page-head">
        <div><h1>Dashboard</h1><p class="muted">History of triage iterations and where each bug stands in Jira.</p></div>
        <a class="btn primary" routerLink="/runs">New triage run</a>
      </div>

      @if (error()) { <div class="alert bad">{{ error() }}</div> }
      @if (cfg(); as c) {
        @if (!c.anthropicConfigured) { <div class="alert warn">ANTHROPIC_API_KEY is not set on the server — AI triage is unavailable.</div> }
        @if (!c.jiraConfigured) { <div class="alert warn">Jira credentials are not set on the server — bugs cannot be created in Jira.</div> }
      }

      @if (data(); as d) {
        <div class="grid kpis">
          <div class="kpi"><div class="v">{{ d.totals.runs }}</div><div class="l">Iterations</div></div>
          <div class="kpi"><div class="v">{{ d.totals.failedTests }}</div><div class="l">Failed tests triaged</div></div>
          <div class="kpi"><div class="v">{{ d.totals.bugs }}</div><div class="l">Bugs (after merging)</div></div>
          <div class="kpi"><div class="v">{{ d.totals.pending }}</div><div class="l">Awaiting Jira</div></div>
          <div class="kpi ok"><div class="v">{{ d.totals.created }}</div><div class="l">Created in Jira</div></div>
          <div class="kpi"><div class="v">{{ d.totals.linked }}</div><div class="l">Linked to existing issues</div></div>
          <div class="kpi" [class.bad]="d.totals.jiraFailed + d.totals.partial > 0">
            <div class="v">{{ d.totals.jiraFailed + d.totals.partial }}</div><div class="l">Jira failures to retry</div>
          </div>
        </div>

        <div class="grid two">
          <section class="card"><h2>Test results per iteration</h2>
            <app-stacked-bars [rows]="resultRows()" [series]="resultSeries" label="Test results per iteration" /></section>
          <section class="card"><h2>Bug status per iteration</h2>
            <app-stacked-bars [rows]="bugRows()" [series]="bugSeries" label="Bug status per iteration" /></section>
        </div>

        <section class="card">
          <h2>Iteration history</h2>
          <div class="scroll">
            <table>
              <thead><tr><th>Iteration</th><th>Date</th><th class="num">Tests</th><th class="num">Failed</th><th class="num">Bugs</th>
                <th class="num">Merged</th><th class="num">In Jira</th><th class="num">Jira failed</th><th class="num">Pending</th><th></th></tr></thead>
              <tbody>
                @for (h of history(); track h.id) {
                  <tr>
                    <td><a [routerLink]="['/runs', h.id]">{{ h.name }}</a></td>
                    <td class="muted">{{ h.uploadedAt | date: 'medium' }}</td>
                    <td class="num">{{ h.totalTests }}</td>
                    <td class="num">{{ h.failed }}</td>
                    <td class="num">{{ h.bugs }}</td>
                    <td class="num">{{ h.merged }}</td>
                    <td class="num">{{ h.created + h.partial }}</td>
                    <td class="num">
                      @if (h.jiraFailed + h.partial > 0) { <span class="badge bad">{{ h.jiraFailed + h.partial }}</span> } @else { 0 }
                    </td>
                    <td class="num">{{ h.pending }}</td>
                    <td>
                      @if (h.status === 'Triaged' && h.bugs > 0 && h.pending === 0 && h.jiraFailed + h.partial === 0) { <span class="badge ok">Done</span> }
                      @else if (h.status === 'Triaged') { <span class="badge info">In progress</span> }
                      @else { <span class="badge warn">{{ h.status }}</span> }
                    </td>
                  </tr>
                } @empty { <tr><td colspan="10" class="muted">No iterations yet. Upload a Playwright report to get started.</td></tr> }
              </tbody>
            </table>
          </div>
        </section>
      }
    </div>`,
  styles: `
    .two { grid-template-columns: repeat(auto-fit, minmax(340px, 1fr)); margin-bottom: 16px; }
    .scroll { overflow-x: auto; }
  `,
})
export class DashboardPage implements OnInit {
  private api = inject(Api);
  data = signal<Dashboard | null>(null);
  cfg = signal<SystemConfig | null>(null);
  error = signal('');

  readonly resultSeries: Series[] = [
    { label: 'Passed', color: '#2f9e6a' }, { label: 'Failed', color: '#d1483f' },
    { label: 'Flaky', color: '#e0a030' }, { label: 'Skipped', color: '#8a96a8' },
  ];
  readonly bugSeries: Series[] = [
    { label: 'Created in Jira', color: '#2f9e6a' }, { label: 'Partially created', color: '#e0a030' },
    { label: 'Jira failed', color: '#d1483f' }, { label: 'Pending', color: '#5b84d6' }, { label: 'Merged', color: '#8a96a8' },
  ];

  /** Newest first for the table; charts use the latest 15 in chronological order. */
  history = computed(() => [...(this.data()?.history ?? [])].reverse());
  private recent = computed(() => (this.data()?.history ?? []).slice(-15));
  resultRows = computed<BarRow[]>(() => this.recent().map((h) => ({
    label: '#' + h.id, title: `${h.name}: ${h.passed} passed, ${h.failed} failed, ${h.flaky} flaky, ${h.skipped} skipped`,
    values: [h.passed, h.failed, h.flaky, h.skipped],
  })));
  bugRows = computed<BarRow[]>(() => this.recent().map((h) => ({
    label: '#' + h.id, title: `${h.name}: ${h.created} created, ${h.partial} partial, ${h.jiraFailed} failed, ${h.pending} pending, ${h.merged} merged`,
    values: [h.created, h.partial, h.jiraFailed, h.pending, h.merged],
  })));

  async ngOnInit() {
    try {
      const [d, c] = await Promise.all([this.api.dashboard(), this.api.config()]);
      this.data.set(d);
      this.cfg.set(c);
    } catch (e) {
      this.error.set(errorText(e));
    }
  }
}
