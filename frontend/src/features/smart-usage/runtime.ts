export function runtimeHours(energyKwh: number, watts: number): number | null {
  return energyKwh <= 0 ? 0 : watts <= 0 ? null : energyKwh / (watts / 1000);
}
export function duration(hours: number | null): string {
  if (hours === null) return "No finite limit";
  if (!Number.isFinite(hours)) return "Unavailable";
  const minutes = Math.max(0, Math.round(hours * 60));
  if (minutes < 60) return `${minutes} min`;
  if (hours > 24) {
    const days = Math.floor(minutes / 1440);
    const remainingHours = Math.floor((minutes % 1440) / 60);
    const remainingMinutes = minutes % 60;
    return [
      `${days} ${days === 1 ? "day" : "days"}`,
      remainingHours ? `${remainingHours} hr` : "",
      remainingMinutes ? `${remainingMinutes} min` : "",
    ]
      .filter(Boolean)
      .join(" ");
  }
  return `${Math.floor(minutes / 60)} hr${minutes % 60 ? ` ${minutes % 60} min` : ""}`;
}
export function localEndTime(start: string, hours: number | null): string {
  if (hours === null) return "No estimated end at zero measured load";
  const date = new Date(new Date(start).getTime() + hours * 3600000);
  if (!Number.isFinite(date.getTime())) return "End time unavailable";
  return new Intl.DateTimeFormat(undefined, {
    weekday: "short",
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit",
    timeZoneName: "short",
  }).format(date);
}
