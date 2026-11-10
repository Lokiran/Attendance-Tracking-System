/** Thin fetch wrapper: cookie auth, CSRF header, and a consistent error type. */

const BASE = (import.meta.env.VITE_API_BASE_URL as string | undefined) ?? "/api";
const CSRF_COOKIE = "ats_csrf";
const SAFE = new Set(["GET", "HEAD", "OPTIONS"]);

export class ApiError extends Error {
  constructor(
    public status: number,
    public code: string,
    message: string,
    public details?: unknown,
    public requestId?: string | null,
  ) {
    super(message);
  }
}

let csrfToken: string | null = null;
export function setCsrfToken(token: string | null) {
  csrfToken = token;
}

function readCookie(name: string): string | null {
  const match = document.cookie.split("; ").find((c) => c.startsWith(`${name}=`));
  return match ? decodeURIComponent(match.slice(name.length + 1)) : null;
}

export type Params = Record<string, string | number | boolean | null | undefined>;

export function buildUrl(path: string, params?: Params): string {
  const qs = new URLSearchParams();
  for (const [k, v] of Object.entries(params ?? {})) {
    if (v !== undefined && v !== null && v !== "") qs.set(k, String(v));
  }
  const q = qs.toString();
  return `${BASE}${path}${q ? `?${q}` : ""}`;
}

async function request(method: string, path: string, opts: { params?: Params; body?: unknown } = {}) {
  const headers: Record<string, string> = { Accept: "application/json" };
  const isForm = opts.body instanceof FormData; // the browser sets the multipart boundary itself
  if (opts.body !== undefined && !isForm) headers["Content-Type"] = "application/json";
  if (!SAFE.has(method)) {
    const token = csrfToken ?? readCookie(CSRF_COOKIE);
    if (token) headers["X-CSRF-Token"] = token;
  }
  let res: Response;
  try {
    res = await fetch(buildUrl(path, opts.params), {
      method,
      headers,
      credentials: "include",
      body: isForm ? (opts.body as FormData) : opts.body !== undefined ? JSON.stringify(opts.body) : undefined,
    });
  } catch {
    throw new ApiError(0, "network_error", "Cannot reach the server. Check your connection and try again.");
  }
  if (!res.ok) {
    let payload: { error?: { code?: string; message?: string; details?: unknown }; request_id?: string } = {};
    try {
      payload = await res.json();
    } catch {
      /* non-JSON error body */
    }
    const err = new ApiError(
      res.status,
      payload.error?.code ?? "http_error",
      payload.error?.message ??
        (res.status >= 500 ? "The server encountered an error. Please try again." : `Request failed (${res.status})`),
      payload.error?.details,
      payload.request_id,
    );
    if (res.status === 401 && path !== "/auth/login") {
      window.dispatchEvent(new CustomEvent("ats:unauthorized", { detail: err }));
    }
    throw err;
  }
  return res;
}

export const api = {
  async get<T>(path: string, params?: Params): Promise<T> {
    return (await request("GET", path, { params })).json();
  },
  async post<T>(path: string, body?: unknown): Promise<T> {
    return (await request("POST", path, { body: body ?? {} })).json();
  },
  async put<T>(path: string, body: unknown): Promise<T> {
    return (await request("PUT", path, { body })).json();
  },
  async patch<T>(path: string, body: unknown): Promise<T> {
    return (await request("PATCH", path, { body })).json();
  },
  async del<T>(path: string): Promise<T> {
    return (await request("DELETE", path)).json();
  },
  /** Uploads a file as multipart form data (field name "file"), with the CSRF header like any write. */
  async upload<T>(path: string, file: File): Promise<T> {
    const form = new FormData();
    form.append("file", file);
    return (await request("POST", path, { body: form })).json();
  },
  /** Downloads a CSV (or other attachment) produced by the backend. */
  async download(path: string, params?: Params): Promise<string> {
    const res = await request("GET", path, { params });
    const blob = await res.blob();
    const disposition = res.headers.get("content-disposition") ?? "";
    const filename = /filename="([^"]+)"/.exec(disposition)?.[1] ?? "report.csv";
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    return filename;
  },
};

export function errorMessage(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  if (err instanceof Error) return err.message;
  return "Something went wrong";
}
