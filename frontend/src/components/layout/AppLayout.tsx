import { clsx } from "clsx";
import {
  Building,
  Building2,
  CalendarCheck,
  CalendarDays,
  ChevronsLeft,
  ChevronsRight,
  ClipboardCheck,
  ClipboardList,
  FileSpreadsheet,
  FileUp,
  LayoutDashboard,
  LogOut,
  Menu,
  Monitor,
  Moon,
  Network,
  ScrollText,
  Settings2,
  Sun,
  UserCog,
  Users,
  X,
  type LucideIcon,
} from "lucide-react";
import { Suspense, useEffect, useRef, useState } from "react";
import { NavLink, Outlet, useLocation, useNavigate } from "react-router-dom";

import { useAuth } from "@/auth/AuthContext";
import { Avatar } from "@/components/ui/Misc";
import { PageSkeleton } from "@/components/ui/Skeleton";
import { useToast } from "@/components/ui/Toast";
import { todayInOffice } from "@/lib/dates";
import { formatDate, ROLE_LABEL } from "@/lib/format";
import type { Role } from "@/lib/types";

interface NavItem {
  to: string;
  label: string;
  icon: LucideIcon;
  roles?: Role[];
  end?: boolean;
}

const SECTIONS: { title: string; items: NavItem[] }[] = [
  {
    title: "My workspace",
    items: [
      { to: "/", label: "My dashboard", icon: LayoutDashboard, end: true },
      { to: "/my/requests", label: "My requests", icon: ClipboardList },
      { to: "/my/wfo", label: "Office days", icon: Building },
    ],
  },
  {
    title: "Team",
    items: [
      { to: "/team", label: "Team attendance", icon: Users, roles: ["MANAGER", "ADMIN"], end: true },
      { to: "/team/wfo", label: "Team office days", icon: Building, roles: ["MANAGER", "ADMIN"] },
      { to: "/approvals", label: "Approvals", icon: ClipboardCheck, roles: ["MANAGER", "ADMIN"] },
      { to: "/reports", label: "Reports", icon: FileSpreadsheet, roles: ["MANAGER", "ADMIN"] },
    ],
  },
  {
    title: "Administration",
    items: [
      { to: "/admin", label: "Organisation", icon: Building2, roles: ["ADMIN"], end: true },
      { to: "/admin/wfo", label: "Org office days", icon: Building, roles: ["ADMIN"] },
      { to: "/admin/attendance-import", label: "Import attendance", icon: FileUp, roles: ["ADMIN"] },
      { to: "/admin/hierarchy", label: "Hierarchy", icon: Network, roles: ["ADMIN"] },
      { to: "/admin/employees", label: "Employees", icon: UserCog, roles: ["ADMIN"] },
      { to: "/admin/departments", label: "Departments", icon: Building2, roles: ["ADMIN"] },
      { to: "/admin/schedules", label: "Schedules", icon: CalendarDays, roles: ["ADMIN"] },
      { to: "/admin/holidays", label: "Holidays", icon: CalendarCheck, roles: ["ADMIN"] },
      { to: "/admin/policy", label: "Attendance policy", icon: Settings2, roles: ["ADMIN"] },
      { to: "/admin/audit", label: "Audit log", icon: ScrollText, roles: ["ADMIN"] },
    ],
  },
];

type Theme = "system" | "light" | "dark";
const THEME_KEY = "ats-color-theme";

function readPref(key: string, fallback: string): string {
  try {
    return localStorage.getItem(key) ?? fallback;
  } catch {
    return fallback;
  }
}
function writePref(key: string, value: string) {
  try {
    localStorage.setItem(key, value);
  } catch {
    /* storage unavailable: preference lasts for this page only */
  }
}

/** Light is the default; "system" follows the OS (see index.css and the boot script in index.html). */
function applyTheme(t: Theme) {
  document.documentElement.dataset.theme = t;
}

function Sidebar({ collapsed, onNavigate }: { collapsed: boolean; onNavigate?: () => void }) {
  const { user } = useAuth();
  return (
    <nav aria-label="Main" className="flex h-full flex-col gap-5 overflow-y-auto px-3 py-4">
      <div className={clsx("flex items-center gap-2 px-2", collapsed && "justify-center px-0")}>
        <span className="inline-flex size-8 items-center justify-center rounded-lg bg-brand text-on-brand" aria-hidden>
          <CalendarCheck className="size-4" />
        </span>
        {!collapsed && (
          <div className="leading-tight">
            <div className="text-sm font-semibold text-on-secondary">Nexa Attendance</div>
            <div className="text-[11px] text-on-secondary-muted">Nexa Technologies</div>
          </div>
        )}
      </div>
      {SECTIONS.map((section) => {
        const items = section.items.filter((i) => !i.roles || (user && i.roles.includes(user.role)));
        if (!items.length) return null;
        return (
          <div key={section.title}>
            {!collapsed && <div className="mb-1 px-2 text-[11px] font-semibold uppercase tracking-wider text-on-secondary-muted">{section.title}</div>}
            <ul className="space-y-0.5">
              {items.map((item) => (
                <li key={item.to}>
                  <NavLink
                    to={item.to}
                    end={item.end}
                    onClick={onNavigate}
                    title={collapsed ? item.label : undefined}
                    className={({ isActive }) =>
                      clsx(
                        "group relative flex items-center gap-2.5 rounded-md px-2 py-1.5 text-sm transition-colors",
                        collapsed && "justify-center",
                        isActive
                          ? "active bg-brand font-medium text-on-brand shadow-sm"
                          : "text-on-secondary-muted hover:bg-secondary-raised hover:text-on-secondary",
                      )
                    }
                  >
                    <item.icon className="size-4 shrink-0 text-on-secondary-muted group-[.active]:text-on-brand" aria-hidden />
                    {collapsed ? <span className="sr-only">{item.label}</span> : item.label}
                  </NavLink>
                </li>
              ))}
            </ul>
          </div>
        );
      })}
    </nav>
  );
}

function ProfileMenu() {
  const { user, logout } = useAuth();
  const toast = useToast();
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const close = (e: MouseEvent) => !ref.current?.contains(e.target as Node) && setOpen(false);
    const esc = (e: KeyboardEvent) => e.key === "Escape" && setOpen(false);
    document.addEventListener("mousedown", close);
    document.addEventListener("keydown", esc);
    return () => {
      document.removeEventListener("mousedown", close);
      document.removeEventListener("keydown", esc);
    };
  }, []);
  if (!user) return null;
  return (
    <div className="relative" ref={ref}>
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-haspopup="menu"
        aria-expanded={open}
        className="flex items-center gap-2 rounded-lg px-1.5 py-1 hover:bg-secondary-raised"
      >
        <Avatar name={user.full_name} size="sm" />
        <span className="hidden text-left leading-tight sm:block">
          <span className="block text-sm font-medium text-on-secondary">{user.full_name}</span>
          <span className="block text-[11px] text-on-secondary-muted">{ROLE_LABEL[user.role]}</span>
        </span>
      </button>
      {open && (
        <div role="menu" className="absolute right-0 z-40 mt-2 w-60 rounded-xl border border-line bg-surface p-1.5 shadow-lg">
          <div className="border-b border-line px-2.5 pb-2 pt-1">
            <div className="text-sm font-medium text-ink">{user.full_name}</div>
            <div className="truncate text-xs text-ink-3">{user.email}</div>
          </div>
          <button
            role="menuitem"
            className="mt-1 flex w-full items-center gap-2 rounded-lg px-2.5 py-2 text-sm text-ink hover:bg-surface-2"
            onClick={() => {
              setOpen(false);
              navigate("/profile");
            }}
          >
            <UserCog className="size-4" aria-hidden /> Profile & password
          </button>
          <button
            role="menuitem"
            className="flex w-full items-center gap-2 rounded-lg px-2.5 py-2 text-sm text-ink hover:bg-surface-2"
            onClick={async () => {
              setOpen(false);
              try {
                await logout();
                navigate("/login", { replace: true });
              } catch (e) {
                toast.error(e);
              }
            }}
          >
            <LogOut className="size-4" aria-hidden /> Sign out
          </button>
        </div>
      )}
    </div>
  );
}

function ThemeToggle() {
  const [theme, setTheme] = useState<Theme>(() => readPref(THEME_KEY, "light") as Theme);
  const next: Record<Theme, Theme> = { light: "dark", dark: "system", system: "light" };
  const Icon = theme === "light" ? Sun : theme === "dark" ? Moon : Monitor;
  return (
    <button
      type="button"
      onClick={() => {
        const t = next[theme];
        setTheme(t);
        writePref(THEME_KEY, t);
        applyTheme(t);
      }}
      className="rounded-lg p-2 text-on-secondary-muted hover:bg-secondary-raised hover:text-on-secondary"
      aria-label={"Theme: " + theme + ". Switch to " + next[theme]}
      title={"Theme: " + theme}
    >
      <Icon className="size-4" />
    </button>
  );
}

export function AppLayout() {
  const [collapsed, setCollapsed] = useState(() => readPref("ats-sidebar", "open") === "collapsed");
  const [mobileOpen, setMobileOpen] = useState(false);
  const location = useLocation();
  useEffect(() => setMobileOpen(false), [location.pathname]);

  return (
    <div className="flex min-h-screen">
      <a href="#main" className="sr-only focus:not-sr-only focus:fixed focus:left-3 focus:top-3 focus:z-50 focus:rounded-lg focus:bg-surface focus:px-3 focus:py-2">
        Skip to content
      </a>
      <aside
        className={clsx(
          "sticky top-0 hidden h-screen shrink-0 border-r border-secondary-line bg-secondary transition-[width] lg:block",
          collapsed ? "w-16" : "w-60",
        )}
      >
        <Sidebar collapsed={collapsed} />
      </aside>
      {mobileOpen && (
        <div className="fixed inset-0 z-40 lg:hidden">
          <div className="absolute inset-0 bg-black/40" onClick={() => setMobileOpen(false)} aria-hidden />
          <aside className="absolute inset-y-0 left-0 w-64 border-r border-secondary-line bg-secondary" aria-label="Navigation drawer">
            <button onClick={() => setMobileOpen(false)} className="absolute right-2 top-3 rounded-md p-1.5 text-on-secondary-muted hover:bg-secondary-raised hover:text-on-secondary" aria-label="Close navigation">
              <X className="size-4" />
            </button>
            <Sidebar collapsed={false} onNavigate={() => setMobileOpen(false)} />
          </aside>
        </div>
      )}
      <div className="flex min-w-0 flex-1 flex-col">
        <header className="sticky top-0 z-30 flex h-14 items-center gap-2 border-b border-secondary-line bg-secondary px-4 text-on-secondary sm:px-6">
          <button onClick={() => setMobileOpen(true)} className="rounded-lg p-2 text-on-secondary-muted hover:bg-secondary-raised hover:text-on-secondary lg:hidden" aria-label="Open navigation">
            <Menu className="size-5" />
          </button>
          <button
            onClick={() => {
              setCollapsed((c) => {
                writePref("ats-sidebar", c ? "open" : "collapsed");
                return !c;
              });
            }}
            className="hidden rounded-lg p-2 text-on-secondary-muted hover:bg-secondary-raised hover:text-on-secondary lg:inline-flex"
            aria-label={collapsed ? "Expand sidebar" : "Collapse sidebar"}
          >
            {collapsed ? <ChevronsRight className="size-4" /> : <ChevronsLeft className="size-4" />}
          </button>
          <div className="hidden text-sm text-on-secondary-muted sm:block">{formatDate(todayInOffice(), { weekday: "long" })}</div>
          <div className="ml-auto flex items-center gap-1">
            <ThemeToggle />
            <ProfileMenu />
          </div>
        </header>
        <main id="main" className="mx-auto w-full max-w-[1400px] flex-1 px-4 py-5 sm:px-6 sm:py-6">
          <Suspense fallback={<PageSkeleton />}>
            <Outlet />
          </Suspense>
        </main>
      </div>
    </div>
  );
}
