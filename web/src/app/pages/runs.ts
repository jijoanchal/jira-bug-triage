import { DatePipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Api } from '../core/api';
import { errorText } from '../core/auth';
import { RunSummary } from '../core/models';

@Component({
  selector: 'app-runs',
  imports: [FormsModule, DatePipe, RouterLink],
  template: `
    <div class="page">
      <div class="page-head"><div><h1>Triage runs</h1>
        <p class="muted">Upload a Playwright JSON report, zephyr-results.json or zephyr-upload.csv. Failed tests are cleansed and written up as draft bugs by AI.</p></div></div>

      <section class="card">
        <h2>New run</h2>
        @if (error()) { <div class="alert bad" role="alert">{{ error() }}</div> }
        @if (stage()) { <div class="alert info" role="status">{{ stage() }}</div> }
        <div class="grid form">
          <label class="field">Run name (optional)
            <input [(ngModel)]="name" placeholder="e.g. Release 4.2 regression" [disabled]="busy()" />
          </label>
          <label class="field">Results file: Playwright JSON, zephyr-results.json or zephyr-upload.csv *
            <input type="file" accept=".json,.csv,application/json,text/csv" (change)="report.set($any($event.target).files?.[0] ?? null)" [disabled]="busy()" />
          </label>
          <label class="field">Zephyr steps (optional zephyr-upload.csv, adds test steps for the AI)
            <input type="file" accept=".csv,text/csv" (change)="steps.set($any($event.target).files?.[0] ?? null)" [disabled]="busy()" />
          </label>
          <label class="field">Artifacts: files or a .zip (screenshots, videos, traces)
            <input type="file" multiple (change)="files.set(toArray($any($event.target).files))" [disabled]="busy()" />
          </label>
          <label class="field">…or an artifacts folder (e.g. test-results)
            <input type="file" multiple webkitdirectory (change)="folder.set(toArray($any($event.target).files))" [disabled]="busy()" />
          </label>
        </div>
        <p class="muted small">Artifacts are matched to the report by their file path (e.g. test-results/login-chromium/test-failed-1.png).
          {{ files().length + folder().length }} artifact file(s) selected.</p>
        <button class="primary" (click)="start()" [disabled]="busy() || !report()">
          {{ busy() ? 'Working…' : 'Upload & triage with AI' }}
        </button>
      </section>

      <section class="card">
        <h2>All runs</h2>
        <div class="scroll"><table>
          <thead><tr><th>Name</th><th>Uploaded</th><th>By</th><th class="num">Failed</th><th class="num">Bugs</th><th class="num">Merged</th>
            <th class="num">In Jira</th><th class="num">Jira failed</th><th class="num">Pending</th><th>Status</th></tr></thead>
          <tbody>
            @for (r of runs(); track r.run.id) {
              <tr>
                <td><a [routerLink]="['/runs', r.run.id]">{{ r.run.name }}</a></td>
                <td class="muted">{{ r.run.uploadedAt | date: 'medium' }}</td>
                <td class="muted">{{ r.run.uploadedByName }}</td>
                <td class="num">{{ r.run.failed }}</td>
                <td class="num">{{ r.bugs }}</td>
                <td class="num">{{ r.merged }}</td>
                <td class="num">{{ r.created }}@if (r.linked) { <span class="muted small" title="Linked to existing Jira issues instead of creating new ones"> +{{ r.linked }} linked</span> }</td>
                <td class="num">@if (r.jiraFailed) { <span class="badge bad">{{ r.jiraFailed }}</span> } @else { 0 }</td>
                <td class="num">{{ r.pending }}</td>
                <td><span class="badge" [class.ok]="r.run.status === 'Triaged'" [class.bad]="r.run.status === 'TriageFailed'" [class.warn]="r.run.status === 'Uploaded' || r.run.status === 'Triaging'">{{ r.run.status }}</span></td>
              </tr>
            } @empty { <tr><td colspan="10" class="muted">No runs yet.</td></tr> }
          </tbody>
        </table></div>
      </section>
    </div>`,
  styles: `.form { grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); } .scroll { overflow-x: auto; }`,
})
export class RunsPage implements OnInit {
  private api = inject(Api);
  private router = inject(Router);
  runs = signal<RunSummary[]>([]);
  name = '';
  report = signal<File | null>(null);
  steps = signal<File | null>(null);
  files = signal<File[]>([]);
  folder = signal<File[]>([]);
  busy = signal(false);
  stage = signal('');
  error = signal('');

  toArray(l: FileList | null): File[] { return l ? Array.from(l) : []; }

  async ngOnInit() { await this.load(); }

  private async load() {
    try { this.runs.set(await this.api.runs()); } catch (e) { this.error.set(errorText(e)); }
  }

  async start() {
    const report = this.report();
    if (!report) return;
    this.busy.set(true);
    this.error.set('');
    let runId: number | null = null;
    try {
      this.stage.set('Uploading report and artifacts…');
      const up = await this.api.upload(this.name, report, [...this.files(), ...this.folder()], this.steps());
      runId = up.run.id;
      this.stage.set(`Found ${up.run.failed} failed test(s). AI is triaging — this can take a minute…`);
      await this.api.triage(runId);
      await this.router.navigate(['/runs', runId]);
    } catch (e) {
      this.error.set(errorText(e) + (runId ? ' The run was saved; open it to retry triage.' : ''));
      this.stage.set('');
      await this.load();
    } finally {
      this.busy.set(false);
    }
  }
}
