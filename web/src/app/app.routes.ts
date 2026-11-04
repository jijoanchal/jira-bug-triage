import { Routes } from '@angular/router';
import { adminGuard, authGuard } from './core/auth';

export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./pages/login').then((m) => m.Login) },
  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () => import('./pages/shell').then((m) => m.Shell),
    children: [
      { path: '', pathMatch: 'full', loadComponent: () => import('./pages/dashboard').then((m) => m.DashboardPage) },
      { path: 'runs', loadComponent: () => import('./pages/runs').then((m) => m.RunsPage) },
      { path: 'runs/:id', loadComponent: () => import('./pages/run-detail').then((m) => m.RunDetailPage) },
      { path: 'runs/:id/report', loadComponent: () => import('./pages/report').then((m) => m.ReportPage) },
      { path: 'usage', loadComponent: () => import('./pages/usage').then((m) => m.UsagePage) },
      { path: 'users', canActivate: [adminGuard], loadComponent: () => import('./pages/users').then((m) => m.UsersPage) },
      { path: 'profile', loadComponent: () => import('./pages/profile').then((m) => m.ProfilePage) },
    ],
  },
  { path: '**', redirectTo: '' },
];
