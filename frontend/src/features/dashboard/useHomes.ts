import { useCallback, useEffect, useRef, useState } from "react";
import type { Api } from "../../lib/api";
import type { Home } from "../../lib/types";
export function useHomes(api: Api) {
  const [homes, setHomes] = useState<Home[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const sequence = useRef(0);
  const alive = useRef(true);
  const refresh = useCallback(async () => {
    const request = ++sequence.current;
    try {
      const next = await api.homes();
      if (alive.current && request === sequence.current) {
        setHomes(next);
        setError("");
      }
    } catch (e) {
      if (alive.current && request === sequence.current)
        setError((e as Error).message);
    } finally {
      if (alive.current && request === sequence.current) setLoading(false);
    }
  }, [api]);
  useEffect(() => {
    alive.current = true;
    let stopped = false;
    let timer: ReturnType<typeof setTimeout>;
    async function poll() {
      await refresh();
      if (!stopped) timer = setTimeout(poll, 3000);
    }
    void poll();
    return () => {
      stopped = true;
      alive.current = false;
      ++sequence.current;
      clearTimeout(timer);
    };
  }, [refresh]);
  const removeHome = useCallback((id: string) => {
    ++sequence.current;
    setHomes((current) => current.filter((home) => home.id !== id));
  }, []);
  return { homes, loading, error, refresh, removeHome };
}
