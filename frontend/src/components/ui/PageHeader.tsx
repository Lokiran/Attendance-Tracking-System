import type { ReactNode } from "react";

/** Page title band in the secondary (navy) colour — part of the 30 % in the 60-30-10 system. */
export function PageHeader({ title, description, actions, breadcrumb }: {
  title: ReactNode;
  description?: ReactNode;
  actions?: ReactNode;
  breadcrumb?: ReactNode;
}) {
  return (
    <header className="tone-secondary mb-5 flex flex-wrap items-end justify-between gap-4 rounded-xl px-5 py-5 sm:px-6">
      <div className="min-w-0">
        {breadcrumb && <nav aria-label="Breadcrumb" className="mb-1 text-xs text-ink-3">{breadcrumb}</nav>}
        <h1 className="text-xl font-semibold tracking-tight text-ink sm:text-2xl">{title}</h1>
        {description && <p className="mt-1 text-sm text-ink-2">{description}</p>}
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </header>
  );
}
