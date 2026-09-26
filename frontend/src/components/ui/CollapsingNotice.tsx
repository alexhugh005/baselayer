import { useEffect, useRef, useState, type ReactNode } from "react";

export function CollapsingNotice({
  visible,
  children,
}: {
  visible: boolean;
  children: ReactNode;
}) {
  const retained = useRef(children);
  const [mounted, setMounted] = useState(visible);
  useEffect(() => {
    if (visible) {
      retained.current = children;
      setMounted(true);
      return;
    }
    const timer = setTimeout(() => setMounted(false), 360);
    return () => clearTimeout(timer);
  }, [visible, children]);

  return (
    <div
      className={`collapsing-notice${visible ? " is-visible" : ""}`}
      aria-hidden={!visible}
      inert={!visible}
    >
      <div className="collapsing-notice-content">
        {visible ? children : mounted ? retained.current : null}
      </div>
    </div>
  );
}
