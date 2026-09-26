import type { ButtonHTMLAttributes } from "react";
export function Button({
  variant = "primary",
  className = "",
  ...props
}: ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: "primary" | "secondary" | "danger" | "ghost";
}) {
  return <button className={`button ${variant} ${className}`} {...props} />;
}
