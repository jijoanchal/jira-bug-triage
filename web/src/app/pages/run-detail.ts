import { DatePipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api } from '../core/api';
import { errorText } from '../core/auth';
import { Bug, JiraVersion, RunDetail, SystemConfig } from '../core/models';
import { BugCard } from './bug-card';

type Filter = 'active' | 'pending' | 'created' | 'failed' | 'closed' | 'all';
type Dialog = null | { kind: 'merge' } | { kind: 'reopen'; bug: Bug } | { kind: 'jira'; retry?: Bug; all?: boolean };

@Component({
  selector: 'app-run-detail',
  imports: [FormsModule, RouterLink, DatePipe, BugCard],
  template: `
    <div class="page">
      @if (detail(); as d) {
        <div class="page-head">
          <div>
            <h1>{{ d.run.name }}</h1>
            <p class="muted">{{ d.run.uploadedAt | date: 'medium' }} · {{ d.run.uploadedByName }} · {{ d.run.fileName }}</p>
          </div>
          <div class="row">
            <a class="btn" [routerLink]="['/runs', d.run.id, 'report']">Preview / PDF</a>
          </div>
        </div>

        <div class="grid kpis">
          <div class="kpi"><div class="v">{{ d.run.totalTests }}</div><div class="l">Tests in report</div></div>
          <div class="kpi bad"><div class="v">{{ d.run.failed }}</div><div class="l">Failed</div></div>
          <div class="kpi"><div class="v">{{ count('pending') }}</div><div class="l">Bugs awaiting Jira</div></div>
          <div class="kpi ok"><div class="v">{{ count('created') }}</div><div class="l">In Jira (new or existing)</div></div>
          <div class="kpi" [class.bad]="count('failed') > 0"><div class="v">{{ count('failed') }}</div><div class="l">Jira failures</div></div>
        </div>

        @if (noStory() > 0) {
          <div class="alert warn" role="alert"><strong>{{ noStory() }} bug(s) have no story ID.</strong> The report did not name a story for them — open the highlighted “Story ID missing” box on each bug (or use Preview / PDF) and enter it, so they can be linked to their story in Jira.</div>
        }
        @if (message()) { <div class="alert" [class.ok]="!messageBad()" [class.bad]="messageBad()" role="status">{{ message() }}</div> }
        @if (d.run.error) { <div class="alert warn">{{ d.run.error }}</div> }

        @if (needsTriage()) {
          <div class="alert info">
            This run has failures that are not triaged yet.
            <button class="primary" (click)="triage()" [disabled]="busy()">{{ busy() ? 'AI is triaging…' : 'Triage with AI' }}</button>
          </div>
        }

        <div class="card toolbar no-print">
          <label class="row">Show
            <select [ngModel]="filter()" (ngModelChange)="filter.set($event)" aria-label="Filter bugs">
              <option value="active">Active drafts</option><option value="pending">Awaiting Jira</option>
              <option value="created">In Jira (created / linked)</option><option value="failed">Jira failed / partial</option>
              <option value="closed">Merged & discarded</option><option value="all">Everything</option>
            </select>
          </label>
          <button class="link" (click)="selectAll()">Select all awaiting Jira</button>
          <button class="link" (click)="selection.set(emptySet)" [disabled]="selection().size === 0">Clear</button>
          <span class="grow"></span>
          @if (count('failed') > 0) { <button (click)="retryAll()" [disabled]="busy()">Retry all failed ({{ count('failed') }})</button> }
          <button (click)="dialog.set({ kind: 'merge' })" [disabled]="busy() || selection().size < 2">Merge selected ({{ selection().size }})</button>
          <button class="primary" (click)="openJira()" [disabled]="busy() || selection().size < 1">Create in Jira ({{ selection().size }})</button>
        </div>

        @for (b of visible(); track b.id) {
          <app-bug-card [bug]="b" [selected]="selection().has(b.id)" (selectedChange)="toggle(b.id, $event)"
                        (changed)="replace($event)" (retry)="retryOne($event)"
                        (reopen)="dialog.set({ kind: 'reopen', bug: $event })" />
        } @empty { <p class="muted">No bugs match this filter.</p> }
      } @else if (error()) { <div class="alert bad">{{ error() }}</div> }
      @else { <p class="muted">Loading…</p> }
    </div>

    @if (dialog(); as dlg) {
      <div class="modal-back" (click)="dialog.set(null)">
        <div class="modal" role="dialog" aria-modal="true" (click)="$event.stopPropagation()">
          @if (dlg.kind === 'merge') {
            <h2>Merge {{ selection().size }} bugs?</h2>
            <p>AI will combine them into one bug with an updated summary, description and labels. The originals are kept in history as “Merged”.</p>
            <div class="actions"><button (click)="dialog.set(null)">Cancel</button><button class="primary" (click)="merge()">Merge with AI</button></div>
          } @else if (dlg.kind === 'reopen') {
            <h2>Reopen bug #{{ dlg.bug.id }}?</h2>
            <p>The bug becomes editable and “Not sent” again, so you can change its project key and versions and submit it again.
              @if (dlg.bug.jiraKey) { <strong>{{ dlg.bug.jiraKey }}</strong> stays in Jira (it is not deleted or changed); submitting again creates a new issue, so close the old one in Jira if it is no longer needed. }</p>
            <div class="actions"><button (click)="dialog.set(null)">Cancel</button><button class="primary" (click)="reopen(dlg.bug)">Reopen</button></div>
          } @else {
            <h2>{{ dlg.retry ? 'Retry in Jira' : 'Create ' + selection().size + ' bug(s) in Jira' }}</h2>
            @if (!dlg.retry || !dlg.retry.jiraKey) {
              <label class="field">Jira project key
                <input [(ngModel)]="projectKey" (ngModelChange)="onProjectKey()" placeholder="e.g. QA" maxlength="20" />
              </label>
              <label class="field">Affects version (only if your Jira project requires it)
                <input [(ngModel)]="affectsVersion" list="jira-versions" autocomplete="off" placeholder="pick from Jira or type (comma-separate several)" maxlength="200" />
              </label>
              <label class="field">Fix version
                <input [(ngModel)]="fixVersion" list="jira-versions" autocomplete="off" placeholder="pick from Jira or type (comma-separate several)" maxlength="200" />
              </label>
              <datalist id="jira-versions">
                @for (v of versions(); track v.name) { <option [value]="v.name">{{ v.released ? 'released' : 'unreleased' }}</option> }
              </datalist>
              <p class="muted small">{{ versionsNote() }}</p>
            }
            @if (storyMissing() > 0) {
              <div class="alert warn" role="alert"><strong>{{ storyMissing() }} bug(s) have no story ID</strong> and will be created without a story link. Cancel and enter the story under each bug (or in Preview / PDF), or continue and link them later.</div>
            }
            <label class="row small"><input type="checkbox" [(ngModel)]="allowDuplicate" />
              Create new issues even if an open one already exists</label>
            <p class="muted small">Before creating, Jira is searched for an issue in the project that is still Open / Planned for Sprint / In Progress and already tracks the same failure; if one is found the bug is linked to it and no new issue is created.</p>
            <p class="muted small">Issue type: {{ cfg()?.jiraIssueType }} · Site: {{ cfg()?.jiraBaseUrl }}. Labels become Jira labels and artifacts are attached to each issue.</p>
            <div class="actions"><button (click)="dialog.set(null)">Cancel</button>
              <button class="primary" (click)="confirmJira(dlg.retry, dlg.all)" [disabled]="!dlg.retry?.jiraKey && !projectKey.trim()">Confirm</button></div>
          }
        </div>
      </div>
    }`,
  styles: `
    .toolbar { display: flex; gap: 12px; align-items: center; flex-wrap: wrap; margin-bottom: 14px; padding: 10px 14px; }
    .toolbar select { width: auto; margin-left: 6px; }
    .alert button { margin-left: 10px; }
  `,
})
export class RunDetailPage {
  private api = inject(Api);

  id = input.required<string>();
  detail = signal<RunDetail | null>(null);
  cfg = signal<SystemConfig | null>(null);
  error = signal('');
  message = signal('');
  messageBad = signal(false);
  busy = signal(false);
  filter = signal<Filter>('active');
  dialog = signal<Dialog>(null);
  projectKey = '';
  affectsVersion = '';
  fixVersion = '';
  allowDuplicate = false;
  versions = signal<JiraVersion[]>([]);
  versionsNote = signal('');
  private versionsFor = '';
  private versionsTimer: ReturnType<typeof setTimeout> | undefined;
  readonly emptySet = new Set<number>();
  selection = signal<Set<number>>(this.emptySet);

  bugs = computed(() => this.detail()?.bugs ?? []);
  needsTriage = computed(() => {
    const d = this.detail();
    return !!d && d.run.failed > 0 && (d.run.status === 'Uploaded' || d.run.status === 'TriageFailed' || d.bugs.length === 0);
  });
  /** Bugs about to be sent to Jira that have no story to link to. */
  storyMissing = computed(() => {
    const d = this.dialog();
    const ids = d?.kind === 'jira' && d.retry && !d.all ? [d.retry.id] : [...this.selection()];
    return this.bugs().filter((b) => ids.includes(b.id) && b.jiraState !== 'Linked' && !b.storyKey).length;
  });
  noStory = computed(() => this.bugs().filter((b) => b.status === 'Draft' && b.jiraState !== 'Linked' && !b.storyKey).length);
  visible = computed(() => this.bugs().filter((b) => this.matches(b, this.filter())));

  constructor() {
    effect(() => { void this.load(Number(this.id())); });
  }

  count(f: Filter) { return this.bugs().filter((b) => this.matches(b, f)).length; }

  private matches(b: Bug, f: Filter): boolean {
    const open = b.status === 'Draft';
    switch (f) {
      case 'active': return open;
      case 'pending': return open && b.jiraState === 'NotSent';
      case 'created': return open && (b.jiraState === 'Created' || b.jiraState === 'PartiallyCreated' || b.jiraState === 'Linked');
      case 'failed': return open && (b.jiraState === 'Failed' || b.jiraState === 'PartiallyCreated');
      case 'closed': return !open;
      default: return true;
    }
  }

  private async load(id: number) {
    try {
      const [d, c] = await Promise.all([this.api.run(id), this.cfg() ? Promise.resolve(this.cfg()!) : this.api.config()]);
      this.detail.set(d);
      this.cfg.set(c);
      if (!this.projectKey) this.projectKey = c.jiraProjectKey ?? '';
      if (!this.affectsVersion) this.affectsVersion = c.jiraAffectsVersion ?? '';
      // drop selections that are no longer selectable
      const ok = new Set(d.bugs.filter((b) => this.matches(b, 'pending')).map((b) => b.id));
      this.selection.update((s) => new Set([...s].filter((x) => ok.has(x))));
    } catch (e) { this.error.set(errorText(e)); }
  }

  toggle(id: number, on: boolean) {
    this.selection.update((s) => { const n = new Set(s); if (on) n.add(id); else n.delete(id); return n; });
  }
  selectAll() { this.selection.set(new Set(this.bugs().filter((b) => this.matches(b, 'pending')).map((b) => b.id))); }

  replace(b: Bug) {
    this.detail.update((d) => d && ({ ...d, bugs: d.bugs.map((x) => (x.id === b.id ? b : x)) }));
  }

  private note(text: string, bad = false) { this.message.set(text); this.messageBad.set(bad); }

  private async guard(fn: () => Promise<void>) {
    this.busy.set(true); this.message.set('');
    try { await fn(); } catch (e) { this.note(errorText(e), true); } finally { this.busy.set(false); }
  }

  triage() {
    return this.guard(async () => { await this.api.triage(Number(this.id())); await this.load(Number(this.id())); this.note('Triage complete.'); });
  }

  merge() {
    this.dialog.set(null);
    return this.guard(async () => {
      const n = this.selection().size;
      const merged = await this.api.merge([...this.selection()]);
      this.selection.set(this.emptySet);
      await this.load(Number(this.id()));
      this.note(`Merged ${n} bugs into #${merged.id}: “${merged.summary}”.`);
    });
  }

  /** Opens the Jira dialog; a single retry starts from that bug's own project key and versions. */
  openJira(retry?: Bug, all = false) {
    if (retry && !all) {
      this.projectKey = retry.jiraProjectKey ?? this.projectKey;
      this.affectsVersion = retry.jiraAffectsVersion ?? this.affectsVersion;
      this.fixVersion = retry.jiraFixVersion ?? this.fixVersion;
    }
    this.allowDuplicate = false;
    this.dialog.set({ kind: 'jira', retry, all });
    void this.loadVersions();
  }

  onProjectKey() {
    clearTimeout(this.versionsTimer);
    this.versionsTimer = setTimeout(() => void this.loadVersions(), 400);
  }

  /** Fills the version pickers with the project's versions from Jira; typing a version by hand still works. */
  private async loadVersions() {
    const key = this.projectKey.trim().toUpperCase();
    if (!/^[A-Z][A-Z0-9_]{1,19}$/.test(key)) { this.versions.set([]); this.versionsNote.set(''); this.versionsFor = ''; return; }
    if (key === this.versionsFor) return;
    this.versionsFor = key;
    this.versionsNote.set('Loading versions from Jira…');
    try {
      const v = await this.api.jiraVersions(key);
      this.versions.set(v);
      this.versionsNote.set(v.length ? `${v.length} version(s) available in ${key}; pick one or type a name.` : `${key} has no versions in Jira.`);
    } catch (e) {
      this.versions.set([]); this.versionsFor = '';
      this.versionsNote.set(`Could not load versions: ${errorText(e)} You can still type a version name.`);
    }
  }

  reopen(b: Bug) {
    this.dialog.set(null);
    return this.guard(async () => {
      await this.api.reopenJira(b.id);
      await this.load(Number(this.id()));
      this.filter.set('pending');
      this.selection.set(new Set([b.id]));
      this.note(`Bug #${b.id} reopened. Review it and confirm the Jira details below.`);
      this.openJira(this.bugs().find((x) => x.id === b.id));
    });
  }

  retryOne(b: Bug) {
    if (!b.jiraKey) { this.openJira(b); return; } // creation failed, so confirm project key and versions
    return this.confirmJira(b);
  }

  retryAll() {
    const failed = this.bugs().filter((b) => this.matches(b, 'failed'));
    if (failed.some((b) => !b.jiraKey)) { this.openJira(failed.find((b) => !b.jiraKey), true); return; }
    return this.guard(async () => {
      for (const b of failed) await this.api.retryJira(b.id, this.projectKey.trim() || undefined, this.affectsVersion.trim() || undefined, this.fixVersion.trim() || undefined, this.allowDuplicate);
      await this.load(Number(this.id()));
      this.reportOutcome(failed.map((b) => b.id), 'Retried');
    });
  }

  confirmJira(retry?: Bug, all = false) {
    this.dialog.set(null);
    return this.guard(async () => {
      const key = this.projectKey.trim() || undefined;
      const version = this.affectsVersion.trim() || undefined;
      const fix = this.fixVersion.trim() || undefined;
      const allow = this.allowDuplicate;
      if (all) {
        const failed = this.bugs().filter((b) => this.matches(b, 'failed'));
        for (const b of failed) await this.api.retryJira(b.id, key, version, fix, allow);
        await this.load(Number(this.id()));
        this.reportOutcome(failed.map((b) => b.id), 'Retried');
      } else if (retry) {
        await this.api.retryJira(retry.id, key, version, fix, allow);
        await this.load(Number(this.id()));
        this.reportOutcome([retry.id], 'Retried');
      } else {
        const ids = [...this.selection()];
        await this.api.createInJira(ids, key, version, fix, allow);
        this.selection.set(this.emptySet);
        await this.load(Number(this.id()));
        this.reportOutcome(ids, 'Submitted');
      }
    });
  }

  private reportOutcome(ids: number[], verb: string) {
    const bugs = this.bugs().filter((b) => ids.includes(b.id));
    const created = bugs.filter((b) => b.jiraState === 'Created').length;
    const linked = bugs.filter((b) => b.jiraState === 'Linked');
    const bad = bugs.length - created - linked.length;
    const parts = [`${created} created`];
    if (linked.length) parts.push(`${linked.length} linked to existing issues (${linked.map((b) => b.jiraKey).join(', ')}), no new issue created`);
    this.note(bad === 0 ? `${verb} ${bugs.length} bug(s): ${parts.join(', ')}.`
      : `${verb} ${bugs.length} bug(s): ${parts.join(', ')}, ${bad} need attention — open the failed bugs to see the error and retry.`, bad > 0);
    if (bad > 0) this.filter.set('failed');
  }
}
