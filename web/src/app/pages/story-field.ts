import { Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api';
import { errorText } from '../core/auth';
import { Bug } from '../core/models';

/**
 * Shows the Jira story a bug is linked to. When the uploaded CSV/JSON named none, it is flagged in a highlighted box
 * that asks for the story key; the answer is saved on the bug and used to link the Jira issue when it is created.
 */
@Component({
  selector: 'app-story-field',
  imports: [FormsModule],
  template: `
    @if (applicable()) {
      @if (!bug().storyKey || editing()) {
        <div class="story" [class.missing]="!bug().storyKey" role="group" aria-label="Story link">
          @if (!bug().storyKey) {
            <strong>⚠ Story ID missing.</strong>
            <span>The uploaded report names no story for this bug. Enter the Jira story it belongs to (e.g. GCR-123) so the new bug can be linked to it.</span>
          } @else { <strong>Story</strong> }
          <span class="no-print inline">
            <input [ngModel]="value()" (ngModelChange)="value.set($event)" placeholder="e.g. GCR-123 (comma-separate several)" maxlength="120"
                   aria-label="Story key" (keydown.enter)="save()" />
            <button class="primary" (click)="save()" [disabled]="busy() || (!value().trim() && !bug().storyKey)">Save story</button>
            @if (editing()) { <button (click)="editing.set(false)">Cancel</button> }
          </span>
          @if (error()) { <span class="err">{{ error() }}</span> }
        </div>
      } @else {
        <span class="small story-ok">
          Story: <strong class="mono">{{ bug().storyKey }}</strong>
          @if (inJira() && bug().storyLinked) { <span class="badge ok">linked in Jira</span> }
          @else if (inJira()) { <span class="badge warn">not linked yet</span> }
          @if (editable()) { <button class="link small no-print" (click)="edit()">Change</button> }
        </span>
      }
    }`,
  styles: `
    .story { display: flex; flex-wrap: wrap; gap: 8px 10px; align-items: center; margin: 8px 0; padding: 8px 12px; border-radius: 6px; border: 1px solid var(--border); }
    .story.missing { background: var(--warn-bg); border: 2px solid var(--warn); color: var(--warn); }
    .inline { display: inline-flex; gap: 8px; align-items: center; flex-wrap: wrap; }
    .inline input { width: 260px; margin: 0; }
    .err { color: var(--bad); }
    .story-ok { display: inline-flex; gap: 6px; align-items: center; }
  `,
})
export class StoryField {
  private api = inject(Api);

  bug = input.required<Bug>();
  changed = output<Bug>();

  value = signal('');
  editing = signal(false);
  busy = signal(false);
  error = signal('');

  /** Merged, discarded and already-linked-to-an-existing-issue bugs are never linked to a story from here. */
  applicable = computed(() => this.bug().status === 'Draft' && this.bug().jiraState !== 'Linked');
  inJira = computed(() => this.bug().jiraState === 'Created' || this.bug().jiraState === 'PartiallyCreated');
  editable = computed(() => this.applicable());

  edit() { this.value.set(this.bug().storyKey ?? ''); this.error.set(''); this.editing.set(true); }

  async save() {
    this.busy.set(true); this.error.set('');
    try {
      this.changed.emit(await this.api.setStory(this.bug().id, this.value().trim() || null));
      this.editing.set(false); this.value.set('');
    } catch (e) { this.error.set(errorText(e)); } finally { this.busy.set(false); }
  }
}
