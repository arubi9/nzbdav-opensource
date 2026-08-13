import { describe, expect, it } from "vitest";
import { updateFromInput } from "./input-change";

describe("updateFromInput", () => {
  it("captures the input value before React clears currentTarget", () => {
    let currentTarget: { value: string } | null = { value: "news.example.com" };
    let updater: ((current: { Host: string }) => { Host: string }) | undefined;

    updateFromInput(
      { get currentTarget() { return currentTarget as { value: string }; } },
      (value) => { updater = (current) => ({ ...current, Host: value }); },
      "value",
    );
    currentTarget = null;

    expect(updater?.({ Host: "" })).toEqual({ Host: "news.example.com" });
  });
});
