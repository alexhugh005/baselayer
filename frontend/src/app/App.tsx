import { useMemo } from "react";
import { ClerkProvider, SignIn, useAuth, UserButton } from "@clerk/react";

import { createApi } from "../lib/api";
import { OAuthCallback } from "../features/connections/OAuthCallback";
import { Dashboard } from "../features/dashboard/Dashboard";
import { AppShell } from "./AppShell";
import { DevelopmentWorkspace } from "./DevelopmentWorkspace";

function ClerkApp() {
  const { getToken, isLoaded, isSignedIn } = useAuth();
  const api = useMemo(() => createApi(() => getToken()), [getToken]);
  return (
    <AppShell
      account={
        <>
          <UserButton />
          <span>Account</span>
        </>
      }
    >
      {!isLoaded ? (
        <p>Loading your account…</p>
      ) : isSignedIn ? (
        window.location.pathname === "/oauth/home-assistant" ? (
          <OAuthCallback api={api} />
        ) : (
          <Dashboard api={api} />
        )
      ) : (
        <div className="signin">
          <SignIn routing="hash" />
        </div>
      )}
    </AppShell>
  );
}
export function App() {
  const key = import.meta.env.VITE_CLERK_PUBLISHABLE_KEY;
  if (key)
    return (
      <ClerkProvider
        publishableKey={key}
        appearance={{
          variables: {
            colorPrimary: "#b2dd79",
            colorPrimaryForeground: "#1e4d2b",
            colorForeground: "#292826",
            colorMutedForeground: "#54524f",
            colorBackground: "#ffffff",
            colorMuted: "#f0eeeb",
            colorInput: "#ffffff",
            colorInputForeground: "#292826",
            colorBorder: "#d8d7d5",
            colorRing: "#1e4d2b",
            colorDanger: "#c51808",
            colorSuccess: "#1e4d2b",
            fontFamily: '"PP Neue Montreal", Arial, Helvetica, sans-serif',
            borderRadius: "8px",
          },
        }}
      >
        <ClerkApp />
      </ClerkProvider>
    );
  if (import.meta.env.DEV && import.meta.env.VITE_LOCAL_DEMO === "true")
    return <DevelopmentWorkspace />;
  return (
    <AppShell account={null}>
      <h1>Connect your Clerk application</h1>
      <p>
        Set VITE_CLERK_PUBLISHABLE_KEY and configure the API’s Clerk issuer to
        enable sign-in.
      </p>
    </AppShell>
  );
}
