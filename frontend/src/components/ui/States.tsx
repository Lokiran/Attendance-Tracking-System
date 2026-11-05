import { AlertTriangle, Inbox } from "lucide-react";
import type { ReactNode } from "react";

import { errorMessage } from "@/lib/api";

import { Button } from "./Button";

export function EmptyState({ title, description, icon, action }: {
  title: string;
  description?: string;
  icon?: ReactNode;
  action?: ReactNode;
}) {
  return (
    <div className="flex flex-col items-center justify-center px-6 py-10 text-center">
      <div className="mb-3 rounded-full bg-surface-2 p-3 text-ink-3" aria-hidden>
        {icon ?? <Inbox className="size-5" />}
      </div>
      <p className="text-sm font-medium text-ink">{title}</p>
      {description && <p className="mt-1 max-w-sm text-xs text-ink-3">{description}</p>}
      {action && <div className="mt-4">{action}</div>}
    </div>
  );
}

export function ErrorState({ error, onRetry, title = "Could not load data" }: {
  error: unknown;
  onRetry?: () => void;
  title?: string;
}) {
  return (
    <div role="alert" className="flex flex-col items-center justify-center px-6 py-10 text-center">
      <div className="mb-3 rounded-full bg-surface-2 p-3 text-critical" aria-hidden>
        <AlertTriangle className="size-5" />
      </div>
      <p className="text-sm font-medium text-ink">{title}</p>
      <p className="mt-1 max-w-md text-xs text-ink-2">{errorMessage(error)}</p>
      {onRetry && (
        <Button variant="secondary" size="sm" className="mt-4" onClick={onRetry}>
          Try again
        </Button>
      )}
    </div>
  );
}
