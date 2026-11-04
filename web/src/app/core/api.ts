import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  Bug, BudgetStatus, Dashboard, JiraAttempt, JiraVersion, Run, RunDetail, RunSummary, SystemConfig, UsagePolicy, UsageReport, User,
} from './models';

@Injectable({ providedIn: 'root' })
export class Api {
  private http = inject(HttpClient);

  dashboard = () => firstValueFrom(this.http.get<Dashboard>('/api/dashboard'));
  config = () => firstValueFrom(this.http.get<SystemConfig>('/api/system/config'));

  myBudget = () => firstValueFrom(this.http.get<BudgetStatus>('/api/usage/budget'));
  usageMonths = (user?: string) => firstValueFrom(this.http.get<string[]>('/api/usage/months', { params: user ? { user } : {} }));
  usageReport = (month: string, user?: string) =>
    firstValueFrom(this.http.get<UsageReport>('/api/usage/report', { params: user ? { month, user } : { month } }));
  usagePolicy = () => firstValueFrom(this.http.get<UsagePolicy>('/api/usage/policy'));
  setBudget = (scope: string, limitUsd: number | null) => firstValueFrom(this.http.put<void>(`/api/usage/budgets/${scope}`, { limitUsd }));

  runs = () => firstValueFrom(this.http.get<RunSummary[]>('/api/runs'));
  run = (id: number) => firstValueFrom(this.http.get<RunDetail>(`/api/runs/${id}`));
  triage = (id: number) => firstValueFrom(this.http.post<{ triaged: number }>(`/api/runs/${id}/triage`, {}));

  upload(name: string, report: File, artifacts: File[], steps?: File | null) {
    const f = new FormData();
    if (name.trim()) f.append('name', name.trim());
    f.append('report', report, report.name);
    if (steps) f.append('steps', steps, steps.name);
    // webkitRelativePath keeps folder structure, which is what lets paths in the report be matched.
    for (const a of artifacts) f.append('artifacts', a, a.webkitRelativePath || a.name);
    return firstValueFrom(this.http.post<{ run: Run; missingArtifacts: number }>('/api/runs', f));
  }

  updateBug = (id: number, b: Partial<Bug> & { discard?: boolean }) =>
    firstValueFrom(this.http.put<Bug>(`/api/bugs/${id}`, {
      summary: b.summary, description: b.description, labels: b.labels, severity: b.severity, discard: b.discard,
    }));
  setStory = (id: number, storyKey: string | null) => firstValueFrom(this.http.put<Bug>(`/api/bugs/${id}/story`, { storyKey }));
  retriage = (id: number) => firstValueFrom(this.http.post<Bug>(`/api/bugs/${id}/retriage`, {}));
  merge = (bugIds: number[]) => firstValueFrom(this.http.post<Bug>('/api/bugs/merge', { bugIds }));
  createInJira = (bugIds: number[], projectKey?: string, affectsVersion?: string, fixVersion?: string, allowDuplicate = false) =>
    firstValueFrom(this.http.post<Bug[]>('/api/bugs/jira', { bugIds, projectKey, affectsVersion, fixVersion, allowDuplicate }));
  retryJira = (id: number, projectKey?: string, affectsVersion?: string, fixVersion?: string, allowDuplicate = false) =>
    firstValueFrom(this.http.post<Bug>(`/api/bugs/${id}/jira/retry`, { projectKey, affectsVersion, fixVersion, allowDuplicate }));
  reopenJira = (id: number) => firstValueFrom(this.http.post<Bug>(`/api/bugs/${id}/jira/reopen`, {}));
  jiraVersions = (projectKey: string) =>
    firstValueFrom(this.http.get<JiraVersion[]>('/api/jira/versions', { params: { projectKey } }));
  attempts = (id: number) => firstValueFrom(this.http.get<JiraAttempt[]>(`/api/bugs/${id}/attempts`));

  attachmentBlob = (id: number) =>
    firstValueFrom(this.http.get(`/api/attachments/${id}/file`, { responseType: 'blob' }));

  users = () => firstValueFrom(this.http.get<User[]>('/api/users'));
  createUser = (u: { email: string; displayName: string; password: string; role: string }) =>
    firstValueFrom(this.http.post<User>('/api/users', u));
  updateUser = (id: string, u: { displayName: string; role: string; isActive: boolean; newPassword?: string }) =>
    firstValueFrom(this.http.put<User>(`/api/users/${id}`, u));
  deleteUser = (id: string) => firstValueFrom(this.http.delete<void>(`/api/users/${id}`));
}
