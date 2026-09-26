import { useMemo, useState } from "react";
import { AppShell } from "./AppShell";
import { createApi } from "../lib/api";
import { OAuthCallback } from "../features/connections/OAuthCallback";
import { Dashboard } from "../features/dashboard/Dashboard";
import { Button } from "../components/ui/Button";
export function DevelopmentWorkspace() {
  const [token, setToken] = useState(
      () => sessionStorage.getItem("base-layer-demo") ?? "",
    ),
    [draft, setDraft] = useState("");
  const api = useMemo(() => createApi(async () => token), [token]);
  return (
    <AppShell account={<span>Demo home</span>}>
      {token ? (
        <>
          <div className="dev-note">
            Local demo{" "}
            <button
              onClick={() => {
                sessionStorage.removeItem("base-layer-demo");
                setToken("");
              }}
            >
              Sign out
            </button>
          </div>
          {window.location.pathname === "/oauth/home-assistant" ? (
            <OAuthCallback api={api} />
          ) : (
            <Dashboard api={api} />
          )}
        </>
      ) : (
        <form
          className="card demo-login"
          onSubmit={(e) => {
            e.preventDefault();
            sessionStorage.setItem("base-layer-demo", draft);
            setToken(draft);
          }}
        >
          <h1>Local test workspace</h1>
          <p>Enter the token from the local setup script.</p>
          <label className="field">
            Development token
            <input
              type="password"
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              required
            />
          </label>
          <Button>Open workspace</Button>
        </form>
      )}
    </AppShell>
  );
}
