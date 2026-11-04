import { DatePipe } from '@angular/common';
import { Component, OnDestroy, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api } from '../core/api';
import { errorText } from '../core/auth';
import { RunDetail } from '../core/models';
import { Markup } from '../core/widgets';
import { Bug } from '../core/models';
import { StoryField } from './story-field';

/** On-screen preview of the cleansed bugs; the browser's "Save as PDF" produces the PDF export. */
@Component({
  selector: 'app-report',
  imports: [DatePipe, FormsModule, RouterLink, Markup, StoryField],
  template: `
    <div class="page">
      <div class="page-head no-print">
        <div><h1>Report preview</h1><p class="muted">Choose “Save as PDF” as the destination in the print dialog.</p></div>
        <div class="row">
          <label class="row"><input type="checkbox" [ngModel]="withImages()" (ngModelChange)="withImages.set($event)" /> Include screenshots</label>
          <label class="row"><input type="checkbox" [ngModel]="withErrors()" (ngModelChange)="withErrors.set($event)" /> Include raw errors</label>
          <a class="btn" [routerLink]="['/runs', id()]">Back to run</a>
          <button class="primary" (click)="print()">Export PDF</button>
        </div>
      </div>

      @if (error()) { <div class="alert bad">{{ error() }}</div> }
      @if (detail(); as d) {
        <header class="doc-head">
          <h1>{{ d.run.name }} — Bug triage report</h1>
          <p class="muted">
            {{ d.run.uploadedAt | date: 'medium' }} · {{ d.run.totalTests }} tests · {{ d.run.passed }} passed ·
            {{ d.run.failed }} failed · {{ d.run.flaky }} flaky · {{ d.run.skipped }} skipped · {{ bugs().length }} bug(s) after triage
          </p>
        </header>

        @if (missingStories() > 0) {
          <div class="alert warn" role="alert"><strong>{{ missingStories() }} bug(s) have no story ID.</strong> The CSV/JSON did not name a story for them; enter it in the highlighted box under each bug so the Jira bug can be linked to its story.</div>
        }
        @for (b of bugs(); track b.id; let i = $index) {
          <section class="card">
            <div class="row">
              <h2 class="grow" style="margin:0">{{ i + 1 }}. {{ b.summary }}</h2>
              <span class="badge" [class.bad]="b.severity === 'Critical' || b.severity === 'High'">{{ b.severity }}</span>
              @if (b.jiraKey) { <span class="badge ok mono">{{ b.jiraKey }}</span> }
            </div>
            <div class="row" style="margin:6px 0">
              @for (l of b.labels; track l) { <span class="chip">{{ l }}</span> }
            </div>
            <app-story-field [bug]="b" (changed)="replace($event)" />
            <app-markup [text]="b.description" />
            <h3>Affected tests</h3>
            <ul>@for (f of b.failures; track f.id) { <li>{{ f.title }} <span class="muted small">({{ f.project }}, {{ f.file }}:{{ f.line }})</span></li> }</ul>
            @if (withErrors()) {
              @for (f of b.failures; track f.id) { <pre class="code">{{ f.errorMessage }}</pre> }
            }
            @if (withImages()) {
              <div class="imgs">
                @for (f of b.failures; track f.id) {
                  @for (a of f.attachments; track a.id) {
                    @if (images()[a.id]; as src) { <figure><img [src]="src" [alt]="a.fileName" /><figcaption class="small muted">{{ a.fileName }}</figcaption></figure> }
                  }
                }
              </div>
            }
          </section>
        } @empty { <p class="muted">No active bugs to report for this run.</p> }
      }
    </div>`,
  styles: `
    .doc-head { margin-bottom: 16px; }
    .imgs { display: flex; flex-wrap: wrap; gap: 12px; margin-top: 8px; }
    figure { margin: 0; max-width: 100%; }
    img { max-width: 100%; max-height: 420px; border: 1px solid var(--border); border-radius: 6px; }
    @media print { .page { padding: 0; max-width: none; } img { max-height: 300px; } }
  `,
})
export class ReportPage implements OnDestroy {
  private api = inject(Api);
  id = input.required<string>();
  detail = signal<RunDetail | null>(null);
  error = signal('');
  withImages = signal(true);
  withErrors = signal(false);
  images = signal<Record<number, string>>({});

  bugs = computed(() => (this.detail()?.bugs ?? []).filter((b) => b.status === 'Draft'));
  missingStories = computed(() => this.bugs().filter((b) => b.jiraState !== 'Linked' && !b.storyKey).length);

  replace(b: Bug) { this.detail.update((d) => d && ({ ...d, bugs: d.bugs.map((x) => (x.id === b.id ? b : x)) })); }

  constructor() {
    effect(() => { void this.load(Number(this.id())); });
  }

  private async load(id: number) {
    try {
      const d = await this.api.run(id);
      this.detail.set(d);
      const atts = d.bugs.filter((b) => b.status === 'Draft').flatMap((b) => b.failures.flatMap((f) => f.attachments))
        .filter((a) => a.available && a.contentType.startsWith('image/'));
      const entries = await Promise.all(atts.map(async (a) => {
        try { return [a.id, URL.createObjectURL(await this.api.attachmentBlob(a.id))] as const; } catch { return null; }
      }));
      this.images.set(Object.fromEntries(entries.filter((e) => e !== null)));
    } catch (e) { this.error.set(errorText(e)); }
  }

  /** Images must be decoded before the print dialog snapshots the page. */
  print() { setTimeout(() => window.print(), 50); }

  ngOnDestroy() { Object.values(this.images()).forEach((u) => URL.revokeObjectURL(u)); }
}
