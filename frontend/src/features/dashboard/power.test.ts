import { describe, it, expect } from "vitest";
import { formatPower, recommendationIds } from "./power";
describe("power display and recommendations", () => {
  it("never presents unknown as zero", () => {
    expect(formatPower(null)).toBe("Unknown");
    expect(formatPower(0)).toBe("0.00 kW");
  });
  it("only preselects permitted running recommendations", () => {
    expect(
      recommendationIds([
        { entityId: "a", recommended: true, allowed: false, state: "on" },
        { entityId: "b", recommended: true, allowed: true, state: "on" },
        { entityId: "c", recommended: true, allowed: true, state: "off" },
      ]),
    ).toEqual(["b"]);
  });
});
