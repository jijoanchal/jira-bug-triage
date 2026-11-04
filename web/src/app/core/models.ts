export interface User {
  id: string;
  email: string;
  displayName: string;
  role: 'Admin' | 'User';
  isActive: boolean;
  mustChangePassword: boolean;
  createdAt: string;
  lastLoginAt?: string;
}

export interface Run {
  id: number;
  name: string;
  fileName: string;
  uploadedAt: string;
  uploadedByName: string;
  status: string;
  error?: string;
  totalTests: number;
  passed: number;
  failed: number;
  skipped: number;
  flaky: number;
  durationMs: number;
}

export interface RunSummary {
  run: Run;
  bugs: number;
  merged: number;
  discarded: number;
  created: number;
  linked: number;
  jiraFailed: number;
  pending: number;
}

export interface Attachment {
  id: number;
  fileName: string;
  contentType: string;
  size: number;
  available: boolean;
  jiraUploaded: boolean;
  jiraError?: string;
}

export interface Failure {
  id: number;
  title: string;
  file: string;
  line: number;
  project: string;
  status: string;
  retries: number;
  durationMs: number;
  errorMessage: string;
  stack?: string;
  snippet?: string;
  storyKey?: string;
  attachments: Attachment[];
}

export type JiraState = 'NotSent' | 'Created' | 'PartiallyCreated' | 'Failed' | 'Linked';

export interface Bug {
  id: number;
  runId: number;
  summary: string;
  description: string;
  labels: string[];
  severity: 'Critical' | 'High' | 'Medium' | 'Low';
  status: 'Draft' | 'Merged' | 'Discarded';
  mergedIntoId?: number;
  aiNote?: string;
  jiraState: JiraState;
  jiraKey?: string;
  jiraUrl?: string;
  jiraError?: string;
  jiraProjectKey?: string;
  jiraAffectsVersion?: string;
  jiraFixVersion?: string;
  storyKey?: string;
  storyLinked: boolean;
  jiraAttempts: number;
  jiraLastAttemptAt?: string;
  createdAt: string;
  updatedAt: string;
  failures: Failure[];
}

export interface JiraVersion {
  name: string;
  released: boolean;
}

export interface RunDetail {
  run: Run;
  bugs: Bug[];
}

export interface JiraAttempt {
  id: number;
  at: string;
  success: boolean;
  message: string;
  jiraKey?: string;
  by: string;
}

export interface HistoryPoint {
  id: number;
  name: string;
  uploadedAt: string;
  status: string;
  totalTests: number;
  passed: number;
  failed: number;
  skipped: number;
  flaky: number;
  bugs: number;
  merged: number;
  discarded: number;
  created: number;
  linked: number;
  partial: number;
  jiraFailed: number;
  pending: number;
}

export interface Dashboard {
  totals: {
    runs: number;
    failedTests: number;
    bugs: number;
    merged: number;
    created: number;
    linked: number;
    partial: number;
    jiraFailed: number;
    pending: number;
  };
  history: HistoryPoint[];
}

export interface SystemConfig {
  anthropicConfigured: boolean;
  model: string;
  jiraConfigured: boolean;
  jiraBaseUrl?: string;
  jiraProjectKey?: string;
  jiraIssueType: string;
  jiraAffectsVersion?: string;
}

export interface BudgetStatus {
  month: string;
  limitUsd: number | null;
  spentUsd: number;
  percent: number | null;
  level: 'none' | 'ok' | 'warning' | 'over';
  limitSource: 'user' | 'global' | null;
  warningPercent: number;
  enforced: boolean;
}

export interface UsageGroup {
  key: string; calls: number; failed: number; totalTokens: number; inputTokens: number; outputTokens: number;
  costUsd: number; priced: boolean;
}

export interface UsageRecord {
  id: number; timestampUtc: string; userName: string; operation: string; runId?: number; bugId?: number; failureId?: number;
  model: string; status: string; httpStatus?: number; attempts: number; inputTokens: number; cacheWriteTokens: number;
  cacheReadTokens: number; outputTokens: number; totalTokens: number; costUsd: number; priced: boolean; durationMs: number;
  requestId?: string; error?: string;
}

export interface UsageReport {
  month: string;
  user?: string;
  totals: {
    calls: number; ok: number; failed: number; blocked: number; retries: number; inputTokens: number; cacheWriteTokens: number;
    cacheReadTokens: number; outputTokens: number; totalTokens: number; costUsd: number; avgDurationMs: number;
    unpricedCalls: number; users: number;
  };
  byUser: {
    userId?: string; userName: string; displayName?: string; calls: number; failed: number; blocked: number; totalTokens: number;
    costUsd: number; lastUsedUtc: string; monthlyLimitUsd?: number; spentThisMonthUsd: number; percent?: number;
  }[];
  byDay: { day: string; calls: number; totalTokens: number; costUsd: number }[];
  byModel: UsageGroup[];
  byOperation: UsageGroup[];
  userBudgets: { userId: string; displayName: string; email: string; limitUsd?: number; spentUsd: number; percent?: number }[] | null;
  records: UsageRecord[];
  recordsTruncated: boolean;
}

export interface UsagePolicy {
  enforced: boolean;
  warningPercent: number;
  globalLimitUsd: number | null;
  globalLimitSource: 'admin' | 'config' | null;
  models: { id: string; label: string; inputPerMTok: number; outputPerMTok: number; cacheReadPerMTok: number }[];
}
