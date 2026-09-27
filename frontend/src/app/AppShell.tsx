import { useState, type ReactNode } from "react";
import {
  LayoutDashboard,
  ChevronsLeft,
  ChevronsRight,
  Settings,
  Lightbulb,
  CircuitBoard,
  Car,
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
                className={`nav-item${window.location.pathname === "/" ? " active" : ""}`}
                href="/"
                aria-current={
                  window.location.pathname === "/" ? "page" : undefined
                }
                aria-label="Overview"
                title="Overview"
              >
                <LayoutDashboard size={18} />{" "}
                <span className="nav-label">Overview</span>
              </a>
              <a
                className={`nav-item${window.location.pathname === "/smart-usage" ? " active" : ""}`}
                href="/smart-usage"
                aria-current={
                  window.location.pathname === "/smart-usage"
                    ? "page"
                    : undefined
                }
                aria-label="Smart Usage"
                title="Smart Usage"
              >
                <Lightbulb size={18} />
                <span className="nav-label">Smart Usage</span>
              </a>
              <a
                className={`nav-item${window.location.pathname === "/smart-panel" ? " active" : ""}`}
                href="/smart-panel"
                aria-current={
                  window.location.pathname === "/smart-panel"
                    ? "page"
                    : undefined
                }
                aria-label="Smart Panel"
                title="Smart Panel"
              >
                <CircuitBoard size={18} />
                <span className="nav-label">Smart Panel</span>
              </a>
              <a
                className={`nav-item${window.location.pathname === "/ev-charging" ? " active" : ""}`}
                href="/ev-charging"
                aria-current={
                  window.location.pathname === "/ev-charging"
                    ? "page"
                    : undefined
                }
                aria-label="EV charging"
                title="EV charging"
              >
                <Car size={18} />
                <span className="nav-label">EV charging</span>
              </a>
              <a
                className={`nav-item${window.location.pathname === "/settings" ? " active" : ""}`}
                href="/settings"
                aria-current={
                  window.location.pathname === "/settings" ? "page" : undefined
                }
                aria-label="Settings"
                title="Settings"
              >
                <Settings size={18} />
                <span className="nav-label">Settings</span>
              </a>
            </nav>
          </div>
          <div className="sidebar-footer">
            <div className="account">{account}</div>
            <button
              type="button"
              className="sidebar-toggle"
              onClick={toggleSidebar}
              aria-expanded={!collapsed}
              aria-controls="workspace-navigation"
              aria-label={collapsed ? "Expand sidebar" : "Collapse sidebar"}
              title={collapsed ? "Expand sidebar" : "Collapse sidebar"}
            >
              {collapsed ? (
                <ChevronsRight size={18} aria-hidden="true" />
              ) : (
                <ChevronsLeft size={18} aria-hidden="true" />
              )}
              <span className="sidebar-toggle-label">Collapse sidebar</span>
            </button>
          </div>
        </div>
      </aside>
      <main id="main-content">{children}</main>
    </div>
  );
}
