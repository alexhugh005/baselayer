import { useState, type ReactNode } from "react";
import {
  LayoutDashboard,
  ShieldCheck,
  ArrowUpRight,
  PanelLeftClose,
  PanelLeftOpen,
} from "lucide-react";

export function AppShell({
  children,
  account,
}: {
  children: ReactNode;
  account: ReactNode;
}) {
  const [collapsed, setCollapsed] = useState(() => {
    try {
      return localStorage.getItem("base-layer-sidebar-collapsed") === "true";
    } catch {
      return false;
    }
  });
  function toggleSidebar() {
    const next = !collapsed;
    setCollapsed(next);
    try {
      localStorage.setItem("base-layer-sidebar-collapsed", String(next));
    } catch {
      // The toggle remains usable when browser storage is unavailable.
    }
  }
  return (
    <div className={`app-shell${collapsed ? " sidebar-collapsed" : ""}`}>
      <a className="skip-link" href="#main-content">
        Skip to content
      </a>
      <aside className="site-header sidebar" aria-label="Workspace sidebar">
        <div className="header-inner">
          <a className="brand" href="/" aria-label="Base Layer home">
            <img src="/brand/base-logo.svg" alt="Base" width="80" height="30" />
            <span>
              <img
                className="layer-logo"
                src="/brand/layer-logo.svg"
                alt="Layer"
                width="95"
                height="30"
              />
            </span>
          </a>
          <div className="sidebar-navigation">
            <nav id="workspace-navigation" aria-label="Main navigation">
              <a
                className="nav-item active"
                href="/"
                aria-current="page"
                aria-label="Overview"
                title="Overview"
              >
                <LayoutDashboard size={18} />{" "}
                <span className="nav-label">Overview</span>
              </a>
            </nav>
            <button
              type="button"
              className="icon-button sidebar-toggle"
              onClick={toggleSidebar}
              aria-expanded={!collapsed}
              aria-controls="workspace-navigation"
              aria-label={collapsed ? "Expand sidebar" : "Collapse sidebar"}
              title={collapsed ? "Expand sidebar" : "Collapse sidebar"}
            >
              {collapsed ? (
                <PanelLeftOpen size={20} />
              ) : (
                <PanelLeftClose size={20} />
              )}
            </button>
          </div>
          <div className="account">{account}</div>
        </div>
      </aside>
      <main id="main-content">
        {children}
        <footer className="site-footer">
          <div className="footer-brand">
            <ShieldCheck size={20} />
            <strong>Your home. Your control.</strong>
          </div>
          <span>
            Base Layer <ArrowUpRight size={14} /> Less guesswork. More control.
          </span>
        </footer>
      </main>
    </div>
  );
}
