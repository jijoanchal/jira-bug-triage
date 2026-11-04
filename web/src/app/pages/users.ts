import { DatePipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api';
import { Auth, errorText } from '../core/auth';
import { User } from '../core/models';

interface Draft { id?: string; email: string; displayName: string; role: string; isActive: boolean; password: string; }

@Component({
  selector: 'app-users',
  imports: [FormsModule, DatePipe],
  template: `
    <div class="page">
      <div class="page-head"><div><h1>User management</h1><p class="muted">Admins can manage users. New and reset passwords must be changed at next sign-in.</p></div>
        <button class="primary" (click)="add()">Add user</button></div>
      @if (error()) { <div class="alert bad" role="alert">{{ error() }}</div> }

      <section class="card scroll"><table>
        <thead><tr><th>Name</th><th>Email</th><th>Role</th><th>Status</th><th>Last sign-in</th><th></th></tr></thead>
        <tbody>
          @for (u of users(); track u.id) {
            <tr>
              <td>{{ u.displayName }}</td><td>{{ u.email }}</td>
              <td><span class="badge" [class.info]="u.role === 'Admin'">{{ u.role }}</span></td>
              <td><span class="badge" [class.ok]="u.isActive" [class.bad]="!u.isActive">{{ u.isActive ? 'Active' : 'Disabled' }}</span></td>
              <td class="muted">{{ u.lastLoginAt ? (u.lastLoginAt | date: 'medium') : 'Never' }}</td>
              <td class="row"><button (click)="edit(u)">Edit</button>
                @if (u.id !== me()?.id) { <button class="danger" (click)="confirmDelete.set(u)">Delete</button> }</td>
            </tr>
          }
        </tbody>
      </table></section>
    </div>

    @if (draft(); as d) {
      <div class="modal-back" (click)="draft.set(null)">
        <form class="modal" role="dialog" aria-modal="true" (click)="$event.stopPropagation()" (ngSubmit)="save()">
          <h2>{{ d.id ? 'Edit user' : 'Add user' }}</h2>
          @if (formError()) { <div class="alert bad">{{ formError() }}</div> }
          <label class="field">Email<input name="email" type="email" [(ngModel)]="d.email" [disabled]="!!d.id" required /></label>
          <label class="field">Display name<input name="dn" [(ngModel)]="d.displayName" required /></label>
          <label class="field">Role
            <select name="role" [(ngModel)]="d.role"><option>User</option><option>Admin</option></select></label>
          @if (d.id) { <label class="row"><input type="checkbox" name="active" [(ngModel)]="d.isActive" /> Active</label> }
          <label class="field" style="margin-top:10px">{{ d.id ? 'Reset password (leave blank to keep)' : 'Temporary password' }}
            <input name="pw" type="password" [(ngModel)]="d.password" autocomplete="new-password" [required]="!d.id" /></label>
          <div class="actions"><button type="button" (click)="draft.set(null)">Cancel</button><button class="primary" type="submit">Save</button></div>
        </form>
      </div>
    }

    @if (confirmDelete(); as u) {
      <div class="modal-back" (click)="confirmDelete.set(null)">
        <div class="modal" role="dialog" aria-modal="true" (click)="$event.stopPropagation()">
          <h2>Delete {{ u.displayName }}?</h2><p>This cannot be undone. Consider disabling the account instead to keep it for reference.</p>
          <div class="actions"><button (click)="confirmDelete.set(null)">Cancel</button><button class="danger" (click)="remove(u)">Delete</button></div>
        </div>
      </div>
    }`,
  styles: `.scroll { overflow-x: auto; }`,
})
export class UsersPage implements OnInit {
  private api = inject(Api);
  me = inject(Auth).user;
  users = signal<User[]>([]);
  draft = signal<Draft | null>(null);
  confirmDelete = signal<User | null>(null);
  error = signal('');
  formError = signal('');

  async ngOnInit() { await this.load(); }
  private async load() { try { this.users.set(await this.api.users()); } catch (e) { this.error.set(errorText(e)); } }

  add() { this.formError.set(''); this.draft.set({ email: '', displayName: '', role: 'User', isActive: true, password: '' }); }
  edit(u: User) { this.formError.set(''); this.draft.set({ id: u.id, email: u.email, displayName: u.displayName, role: u.role, isActive: u.isActive, password: '' }); }

  async save() {
    const d = this.draft();
    if (!d) return;
    this.formError.set('');
    try {
      if (d.id) await this.api.updateUser(d.id, { displayName: d.displayName, role: d.role, isActive: d.isActive, newPassword: d.password || undefined });
      else await this.api.createUser({ email: d.email, displayName: d.displayName, password: d.password, role: d.role });
      this.draft.set(null);
      await this.load();
    } catch (e) { this.formError.set(errorText(e)); }
  }

  async remove(u: User) {
    this.confirmDelete.set(null);
    try { await this.api.deleteUser(u.id); await this.load(); } catch (e) { this.error.set(errorText(e)); }
  }
}
