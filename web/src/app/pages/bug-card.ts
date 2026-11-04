import { DatePipe } from '@angular/common';
import { Component, computed, effect, inject, input, linkedSignal, model, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api';
import { errorText } from '../core/auth';
import { Attachment, Bug, JiraAttempt } from '../core/models';
import { Markup, TagInput } from '../core/widgets';
import { StoryField } from './story-field';

@Component({
  selector: 'app-bug-card',
  imports: [FormsModule, DatePipe, TagInput, Markup, StoryField],
  template: `
    <article class="card" [class.dim]="bug().status !== 'Draft'">
      <div class="head">
        <input type="checkbox" [checked]="selected()" (change)="selected.set($any($event.target).checked)"
               [disabled]="!selectable()" [attr.aria-label]="'Select bug ' + bug().id" />
        <button class="link title" (click)="open.set(!open())" [attr.aria-expanded]="open()">
          <span class="caret">{{ open() ? '▾' : '▸' }}</span> {{ bug().summary }}
        </button>
        <span class="badge" [class.bad]="bug().severity === 'Critical' || bug().severity === 'High'" [class.warn]="bug().severity === 'Medium'">{{ bug().severity }}</span>
        @switch (bug().status) {
          @case ('Merged') { <span class="badge">Merged into #{{ bug().mergedIntoId }}</span> }
          @case ('Discarded') { <span class="badge">Discarded</span> }
          @default {
            @switch (bug().jiraState) {
              @case ('Created') { <span class="badge ok">Created</span> }
              @case ('Linked') { <span class="badge info" title="An open Jira issue already tracks this failure, so no new issue was created">Already in Jira</span> }
              @case ('PartiallyCreated') { <span class="badge warn">Created, attachments failed</span> }
              @case ('Failed') { <span class="badge bad">Jira failed</span> }
              @default { <span class="badge info">Not sent</span> }
            }
          }
        }
        @if (bug().jiraKey) { <a [href]="bug().jiraUrl" target="_blank" rel="noopener" class="mono">{{ bug().jiraKey }}</a> }
      </div>

      <div class="meta">
        @for (l of bug().labels; track l) { <span class="chip">{{ l }}</span> }
        <span class="muted small">{{ bug().failures.length }} failing test(s) · bug #{{ bug().id }}</span>
        @if (bug().jiraProjectKey) {
          <span class="muted small" title="What this bug was last submitted to Jira with">Jira: {{ bug().jiraProjectKey }}@if (bug().jiraAffectsVersion) { · affects {{ bug().jiraAffectsVersion }} }@if (bug().jiraFixVersion) { · fix {{ bug().jiraFixVersion }} }</span>
        }
        @if (reopenable()) {
          <button class="link small" (click)="reopen.emit(bug())" [disabled]="busy()"
                  title="Make this bug editable and submittable again, e.g. with a different project key or version">Reopen</button>
        }
      </div>

      <div class="story-row">
        <app-story-field [bug]="bug()" (changed)="changed.emit($event)" />
        @if (bug().jiraState === 'Created' && bug().storyKey && !bug().storyLinked) {
          <button class="small" (click)="retry.emit(bug())" [disabled]="busy()">Link to story in Jira</button>
        }
      </div>

      @if (bug().jiraError) {
        <div class="alert bad err" role="alert">
          <div><strong>Jira error:</strong> {{ bug().jiraError }}</div>
          @if (bug().jiraState === 'Failed' || bug().jiraState === 'PartiallyCreated') {
            <button (click)="retry.emit(bug())" [disabled]="busy()">Retry in Jira</button>
          }
        </div>
      }
      @if (bug().aiNote) { <div class="alert warn">{{ bug().aiNote }}
        @if (editable() && bug().failures.length === 1) { <button (click)="retriage()" [disabled]="busy()">Re-triage with AI</button> }
      </div> }

      @if (open()) {
        @if (error()) { <div class="alert bad">{{ error() }}</div> }
        @if (editable()) {
          <label class="field">Summary
            <input [ngModel]="summary()" (ngModelChange)="summary.set($event)" maxlength="250" />
          </label>
          <div class="row2">
            <div class="grow"><label class="field" style="margin-bottom:0">Labels (Jira tags)</label>
              <app-tag-input [(tags)]="labels" /></div>
            <label class="field sev">Severity
              <select [ngModel]="severity()" (ngModelChange)="severity.set($event)">
                @for (s of severities; track s) { <option [value]="s">{{ s }}</option> }
              </select>
            </label>
          </div>
          <div class="row" style="margin:10px 0 4px">
            <strong class="small muted">Description</strong>
            <button class="link small" (click)="preview.set(!preview())">{{ preview() ? 'Edit' : 'Preview' }}</button>
          </div>
          @if (preview()) { <div class="card prev"><app-markup [text]="description()" /></div> }
          @else { <textarea [ngModel]="description()" (ngModelChange)="description.set($event)" aria-label="Description"></textarea> }
        } @else {
          <div class="card prev"><app-markup [text]="bug().description" /></div>
        }

        <h3 style="margin-top:14px">Failing tests & artifacts</h3>
        @for (f of bug().failures; track f.id) {
          <div class="fail">
            <div><strong>{{ f.title }}</strong> <span class="muted small" style="margin-left: 8px">{{ f.project }} · {{ f.file }}:{{ f.line }} · {{ f.retries }} retr{{ f.retries === 1 ? 'y' : 'ies' }}</span></div>
            <pre class="code">{{ f.errorMessage }}</pre>
            <div class="row">
              @for (a of f.attachments; track a.id) {
                @if (a.available) {
                  <button class="link small" (click)="download(a)">📎 {{ a.fileName }} ({{ size(a.size) }})</button>
                } @else { <span class="muted small" title="File was referenced in the report but not uploaded">⚠ {{ a.fileName }} (not provided)</span> }
                @if (a.jiraUploaded) { <span class="badge ok">in Jira</span> }
                @else if (a.jiraError) { <span class="badge bad" [title]="a.jiraError">upload failed</span> }
              }
            </div>
          </div>
        }

        @if (bug().jiraAttempts > 0) {
          <h3 style="margin-top:14px">Jira attempts</h3>
          <table>
            <tbody>
              @for (a of attempts(); track a.id) {
                <tr><td class="muted small">{{ a.at | date: 'medium' }}</td><td>{{ a.by }}</td>
                  <td><span class="badge" [class.ok]="a.success" [class.bad]="!a.success">{{ a.success ? 'Success' : 'Failed' }}</span></td>
                  <td class="small">{{ a.message }}</td></tr>
              }
            </tbody>
          </table>
        }

        @if (bug().status !== 'Merged' && bug().jiraState !== 'Created' && bug().jiraState !== 'PartiallyCreated' && bug().jiraState !== 'Linked') {
          <div class="row actions">
            @if (editable()) {
              <button class="primary" (click)="save()" [disabled]="busy() || !dirty() || !summary().trim()">Save changes</button>
              <button (click)="discard(true)" [disabled]="busy()">Discard bug</button>
              @if (bug().failures.length === 1) { <button (click)="retriage()" [disabled]="busy()">Re-triage with AI</button> }
            }
            @if (bug().status === 'Discarded') { <button (click)="discard(false)" [disabled]="busy()">Restore</button> }
          </div>
        }
      }
    </article>`,
  styles: `
    .card { margin: 0 0 12px; padding: 12px 14px; }
    .card.dim { opacity: 0.7; }
    .head { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }
    .title { text-align: left; font-weight: 600; font-size: 15px; flex: 1 1 280px; color: var(--text); }
    .caret { color: var(--muted); display: inline-block; width: 14px; }
    .meta { display: flex; gap: 6px; flex-wrap: wrap; align-items: center; margin: 6px 0 0 28px; }
    .story-row { margin: 4px 0 0 28px; display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }
    .err { display: flex; justify-content: space-between; align-items: center; gap: 12px; margin: 10px 0 0 28px; }
    .row2 { display: flex; gap: 12px; align-items: flex-end; flex-wrap: wrap; }
    .sev { width: 130px; margin: 0; }
    .prev { background: var(--surface-2); margin: 0; box-shadow: none; }
    .fail { border-top: 1px solid var(--border); padding: 8px 0; }
    .actions { margin-top: 14px; }
    app-tag-input { display: block; margin-top: 4px; }
  `,
})
export class BugCard {
  private api = inject(Api);

  bug = input.required<Bug>();
  selected = model(false);
  changed = output<Bug>();
  retry = output<Bug>();
  reopen = output<Bug>();

  readonly severities = ['Critical', 'High', 'Medium', 'Low'];
  open = signal(false);
  preview = signal(false);
  busy = signal(false);
  error = signal('');
  attempts = signal<JiraAttempt[]>([]);

  // Local edit buffers reset whenever the server copy changes.
  summary = linkedSignal(() => this.bug().summary);
  description = linkedSignal(() => this.bug().description);
  labels = linkedSignal(() => [...this.bug().labels]);
  severity = linkedSignal(() => this.bug().severity as string);

  editable = computed(() => this.bug().status === 'Draft' && (this.bug().jiraState === 'NotSent' || this.bug().jiraState === 'Failed'));
  reopenable = computed(() => this.bug().status === 'Draft' && this.bug().jiraState !== 'NotSent');
  selectable = computed(() => this.bug().status === 'Draft' && this.bug().jiraState === 'NotSent');
  dirty = computed(() => {
    const b = this.bug();
    return this.summary() !== b.summary || this.description() !== b.description ||
      this.severity() !== b.severity || this.labels().join('\u0000') !== b.labels.join('\u0000');
  });

  constructor() {
    // Load the attempt log when the card is opened, and again after each new Jira attempt.
    effect(() => {
      const id = this.bug().id;
      if (this.open() && this.bug().jiraAttempts > 0) {
        this.api.attempts(id).then((a) => this.attempts.set(a)).catch(() => { /* non-critical */ });
      }
    });
  }

  async save() {
    await this.run(async () => this.changed.emit(await this.api.updateBug(this.bug().id, {
      summary: this.summary(), description: this.description(), labels: this.labels(), severity: this.severity() as Bug['severity'],
    })));
  }
  async discard(v: boolean) {
    await this.run(async () => this.changed.emit(await this.api.updateBug(this.bug().id, {
      summary: this.bug().summary, description: this.bug().description, labels: this.bug().labels, discard: v,
    })));
  }
  async retriage() {
    await this.run(async () => this.changed.emit(await this.api.retriage(this.bug().id)));
  }

  async download(a: Attachment) {
    try {
      const blob = await this.api.attachmentBlob(a.id);
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url; link.download = a.fileName; link.click();
      setTimeout(() => URL.revokeObjectURL(url), 5000);
    } catch (e) { this.error.set(errorText(e)); }
  }

  size(n: number) { return n > 1048576 ? (n / 1048576).toFixed(1) + ' MB' : Math.max(1, Math.round(n / 1024)) + ' KB'; }

  private async run(fn: () => Promise<void>) {
    this.busy.set(true); this.error.set('');
    try { await fn(); } catch (e) { this.error.set(errorText(e)); } finally { this.busy.set(false); }
  }
}
