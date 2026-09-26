import { RefreshCw } from "lucide-react";

export function LoadingStatus({ children }: { children: React.ReactNode }) {
  return (
    <span className="loading-status" role="status">
      <RefreshCw size={18} className="spin" aria-hidden="true" />
      {children}
    </span>
  );
}
