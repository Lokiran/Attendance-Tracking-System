import { clsx } from "clsx";
import { Search } from "lucide-react";
import { forwardRef, useEffect, useId, useState, type InputHTMLAttributes, type ReactNode, type SelectHTMLAttributes, type TextareaHTMLAttributes } from "react";

const control =
  "w-full rounded-lg border border-line-strong bg-surface px-3 text-sm text-ink placeholder:text-ink-3 disabled:opacity-60 aria-[invalid=true]:border-critical";

export function Field({ label, error, hint, children, htmlFor, required }: {
  label: string;
  error?: string;
  hint?: string;
  children: ReactNode;
  htmlFor?: string;
  required?: boolean;
}) {
  return (
    <div className="space-y-1">
      <label htmlFor={htmlFor} className="block text-xs font-medium text-ink-2">
        {label}
        {required && <span className="text-critical-text"> *</span>}
      </label>
      {children}
      {error ? (
        <p className="text-xs text-critical-text" role="alert">
          {error}
        </p>
      ) : (
        hint && <p className="text-xs text-ink-3">{hint}</p>
      )}
    </div>
  );
}

export const Input = forwardRef<HTMLInputElement, InputHTMLAttributes<HTMLInputElement>>(function Input({ className, ...rest }, ref) {
  return <input ref={ref} {...rest} className={clsx(control, "h-9", className)} />;
});

export const Select = forwardRef<HTMLSelectElement, SelectHTMLAttributes<HTMLSelectElement>>(function Select(
  { className, children, ...rest },
  ref,
) {
  return (
    <select ref={ref} {...rest} className={clsx(control, "h-9 pr-8", className)}>
      {children}
    </select>
  );
});

export const TextArea = forwardRef<HTMLTextAreaElement, TextareaHTMLAttributes<HTMLTextAreaElement>>(function TextArea(
  { className, ...rest },
  ref,
) {
  return <textarea ref={ref} {...rest} className={clsx(control, "min-h-20 py-2", className)} />;
});

/** Debounced search box. */
export function SearchInput({ value, onChange, placeholder = "Search…", label = "Search", delay = 300 }: {
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  label?: string;
  delay?: number;
}) {
  const id = useId();
  const [text, setText] = useState(value);
  useEffect(() => setText(value), [value]);
  useEffect(() => {
    if (text === value) return;
    const t = setTimeout(() => onChange(text), delay);
    return () => clearTimeout(t);
  }, [text, value, onChange, delay]);
  return (
    <div className="relative min-w-48 flex-1 sm:max-w-xs">
      <label htmlFor={id} className="sr-only">
        {label}
      </label>
      <Search className="pointer-events-none absolute left-2.5 top-2.5 size-4 text-ink-3" aria-hidden />
      <input id={id} type="search" value={text} onChange={(e) => setText(e.target.value)} placeholder={placeholder} className={clsx(control, "h-9 pl-8")} />
    </div>
  );
}

export function Checkbox({ label, ...rest }: InputHTMLAttributes<HTMLInputElement> & { label: string }) {
  const id = useId();
  return (
    <label htmlFor={rest.id ?? id} className="inline-flex cursor-pointer items-center gap-2 text-sm text-ink">
      <input id={rest.id ?? id} type="checkbox" {...rest} className="size-4 rounded border-line-strong accent-[var(--brand)]" />
      {label}
    </label>
  );
}
