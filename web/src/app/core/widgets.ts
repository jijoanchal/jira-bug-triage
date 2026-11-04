import { Component, computed, input, model, signal } from '@angular/core';
import { Block, parseMarkup } from './markup';

/** Tag editor: Enter / comma / blur adds a tag, Backspace on empty input removes the last. */
@Component({
  selector: 'app-tag-input',
  template: `
    <div class="box" [class.disabled]="disabled()">
      @for (t of tags(); track t) {
        <span class="chip">{{ t }}
          @if (!disabled()) { <button type="button" class="x" (click)="remove(t)" [attr.aria-label]="'Remove ' + t">×</button> }
        </span>
      }
      @if (!disabled()) {
        <input [value]="draft()" (input)="draft.set($any($event.target).value)"
               (keydown)="key($event)" (blur)="commit()" placeholder="Add label, press Enter" aria-label="Add label" />
      }
    </div>`,
  styles: `
    .box { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; border: 1px solid var(--border); border-radius: 7px; padding: 5px 8px; background: var(--surface); }
    .box.disabled { background: var(--surface-2); }
    input { border: none; outline: none; flex: 1 1 140px; min-width: 120px; padding: 3px 0; background: transparent; }
    .x { border: none; background: none; padding: 0 2px; cursor: pointer; color: inherit; font-size: 14px; line-height: 1; }
  `,
})
export class TagInput {
  tags = model<string[]>([]);
  disabled = input(false);
  protected draft = signal('');

  /** Jira labels cannot contain spaces. */
  private norm = (s: string) => s.trim().toLowerCase().replace(/[^a-z0-9_.-]+/g, '-').replace(/^-+|-+$/g, '');

  protected key(e: KeyboardEvent) {
    if (e.key === 'Enter' || e.key === ',') { e.preventDefault(); this.commit(); }
    else if (e.key === 'Backspace' && !this.draft() && this.tags().length) this.tags.update((t) => t.slice(0, -1));
  }
  protected commit() {
    const v = this.norm(this.draft());
    this.draft.set('');
    if (v && !this.tags().includes(v)) this.tags.update((t) => [...t, v]);
  }
  protected remove(t: string) { this.tags.update((x) => x.filter((y) => y !== t)); }
}

/** Renders the bug description markup. */
@Component({
  selector: 'app-markup',
  template: `
    @for (b of blocks(); track $index) {
      @switch (b.type) {
        @case ('h') { <h3>{{ $any(b).text }}</h3> }
        @case ('p') { <p>{{ $any(b).text }}</p> }
        @case ('ul') { <ul>@for (i of $any(b).items; track $index) { <li>{{ i }}</li> }</ul> }
        @case ('ol') { <ol>@for (i of $any(b).items; track $index) { <li>{{ i }}</li> }</ol> }
        @case ('code') { <pre class="code">{{ $any(b).text }}</pre> }
      }
    }`,
  styles: `h3 { margin: 12px 0 4px; } p { margin: 4px 0; } ul, ol { margin: 4px 0; padding-left: 22px; }`,
})
export class Markup {
  text = input('');
  protected blocks = computed<Block[]>(() => parseMarkup(this.text()));
}

export interface Series { label: string; color: string; }
export interface BarRow { label: string; title: string; values: number[]; }

/** Dependency-free stacked bar chart. */
@Component({
  selector: 'app-stacked-bars',
  template: `
    @if (rows().length === 0) {
      <p class="muted">No data yet.</p>
    } @else {
      <svg [attr.viewBox]="'0 0 ' + W + ' ' + H" role="img" [attr.aria-label]="label()" preserveAspectRatio="xMidYMid meet">
        @for (t of ticks(); track t.v) {
          <line [attr.x1]="L" [attr.x2]="W" [attr.y1]="t.y" [attr.y2]="t.y" stroke="var(--border)" />
          <text [attr.x]="L - 6" [attr.y]="t.y + 4" text-anchor="end" class="axis">{{ t.v }}</text>
        }
        @for (b of bars(); track b.i) {
          <g>
            <title>{{ b.title }}</title>
            @for (s of b.segs; track s.k) {
              <rect [attr.x]="b.x" [attr.y]="s.y" [attr.width]="bw()" [attr.height]="s.h" [attr.fill]="s.color" rx="2" />
            }
            <text [attr.x]="b.x + bw() / 2" [attr.y]="H - 8" text-anchor="middle" class="axis">{{ b.label }}</text>
          </g>
        }
      </svg>
      <div class="legend">
        @for (s of series(); track s.label) { <span><i [style.background]="s.color"></i>{{ s.label }}</span> }
      </div>
    }`,
  styles: `
    svg { width: 100%; height: auto; max-height: 260px; }
    .axis { font-size: 10px; fill: var(--muted); }
    .legend { display: flex; flex-wrap: wrap; gap: 14px; font-size: 12px; color: var(--muted); margin-top: 6px; }
    .legend i { display: inline-block; width: 10px; height: 10px; border-radius: 2px; margin-right: 5px; }
  `,
})
export class StackedBars {
  rows = input<BarRow[]>([]);
  series = input<Series[]>([]);
  label = input('Chart');

  protected readonly W = 640;
  protected readonly H = 220;
  protected readonly L = 34;
  private readonly top = 10;
  private readonly bottom = 26;

  private max = computed(() => Math.max(1, ...this.rows().map((r) => r.values.reduce((a, b) => a + b, 0))));
  protected bw = computed(() => {
    const slot = (this.W - this.L) / Math.max(1, this.rows().length);
    return Math.min(48, slot * 0.65);
  });

  private y = (v: number) => this.top + (1 - v / this.max()) * (this.H - this.top - this.bottom);

  protected ticks = computed(() => {
    const m = this.max();
    const step = Math.max(1, Math.ceil(m / 4));
    const out: { v: number; y: number }[] = [];
    for (let v = 0; v <= m; v += step) out.push({ v, y: this.y(v) });
    return out;
  });

  protected bars = computed(() => {
    const rows = this.rows();
    const slot = (this.W - this.L) / Math.max(1, rows.length);
    const series = this.series();
    return rows.map((r, i) => {
      let acc = 0;
      const segs = r.values.map((v, k) => {
        const y1 = this.y(acc + v);
        const h = this.y(acc) - y1;
        acc += v;
        return { k, y: y1, h, color: series[k]?.color ?? '#888' };
      }).filter((s) => s.h > 0);
      return { i, x: this.L + slot * i + (slot - this.bw()) / 2, label: r.label, title: r.title, segs };
    });
  });
}
