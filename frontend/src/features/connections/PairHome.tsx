import { useState } from "react";
import { ArrowUpRight, ShieldCheck } from "lucide-react";
import type { Api } from "../../lib/api";
import { Modal } from "../../components/ui/Modal";
import { Button } from "../../components/ui/Button";
export function PairHome({ api, onClose }: { api: Api; onClose: () => void }) {
  const [name, setName] = useState("My home"),
    [url, setUrl] = useState("http://localhost:8123"),
    [busy, setBusy] = useState(false),
    [error, setError] = useState("");
  async function connect() {
    setBusy(true);
    setError("");
    try {
      const result = await api.connect(name, url);
      window.location.assign(result.authorizationUrl);
    } catch (e) {
      setError((e as Error).message);
      setBusy(false);
    }
  }
  return (
    <Modal title="Connect Home Assistant" onClose={onClose}>
      <form
        onSubmit={(e) => {
          e.preventDefault();
          void connect();
        }}
      >
        <div className="connect-illustration">
          <ShieldCheck size={32} />
          <span>Your home, your permission.</span>
        </div>
        <p>
          Sign in directly with Home Assistant to authorize Base Layer. No
          plugin installation needed.
        </p>
        <label className="field">
          Home name
          <input
            value={name}
            maxLength={80}
            onChange={(e) => setName(e.target.value)}
            required
            autoComplete="off"
          />
        </label>
        <label className="field">
          Home Assistant URL
          <input
            type="url"
            value={url}
            onChange={(e) => setUrl(e.target.value)}
            required
            placeholder="https://your-home.example.com"
          />
        </label>
        <p className="muted">
          Your Base Layer server must be able to reach this address. After
          connecting, choose your power meters and which devices Base Layer may
          control.
        </p>
        {error && (
          <p role="alert" className="error">
            {error}
          </p>
        )}
        <Button disabled={busy}>
          {busy ? "Redirecting…" : "Continue to Home Assistant"}{" "}
          <ArrowUpRight size={17} />
        </Button>
      </form>
    </Modal>
  );
}
