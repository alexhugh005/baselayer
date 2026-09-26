import { useEffect, useRef, useState } from "react";
import { ShieldCheck } from "lucide-react";
import type { Api } from "../../lib/api";
import { Button } from "../../components/ui/Button";
export function OAuthCallback({ api }: { api: Api }) {
  const started = useRef(false),
    [error, setError] = useState("");
  useEffect(() => {
    if (started.current) return;
    started.current = true;
    const params = new URLSearchParams(window.location.search);
    const code = params.get("code"),
      state = params.get("state");
    if (!code || !state) {
      setError(
        "Authorization was cancelled or the callback is missing required information.",
      );
      return;
    }
    void api
      .complete(code, state)
      .then((home) => {
        window.location.replace(`/?connected=${encodeURIComponent(home.id)}`);
      })
      .catch((e) => setError((e as Error).message));
  }, [api]);
  return (
    <div className="card onboarding">
      <ShieldCheck size={40} />
      <h1>{error ? "Unable to connect" : "Connecting your home…"}</h1>
      <p>
        {error ||
          "Verifying your authorization and discovering devices. Your Home Assistant tokens stay on the server."}
      </p>
      {error && (
        <Button onClick={() => window.location.assign("/")}>
          Return and try again
        </Button>
      )}
    </div>
  );
}
