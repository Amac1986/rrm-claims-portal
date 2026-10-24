import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import axe from "axe-core";
import { App } from "./App";
import { Confirmation } from "./Confirmation";

const receipt = {
  claimId: "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  claimNumber: "CLM-20261026-K7F3QX",
  submittedAt: "2026-10-26T14:03:11.482Z",
  status: "Received",
};

async function expectNoAxeViolations(container: HTMLElement) {
  const results = await axe.run(container, {
    // jsdom has no layout/canvas, so axe cannot compute rendered contrast; that stays a manual check.
    rules: { "color-contrast": { enabled: false } },
  });
  const violations = results.violations.map(
    (v) => `${v.id} (${v.impact}): ${v.help} [${v.nodes.map((n) => n.target.join(" ")).join(", ")}]`,
  );
  expect(violations).toEqual([]);
}

describe("automated accessibility checks", () => {
  const fetchMock = vi.fn();
  beforeEach(() => vi.stubGlobal("fetch", fetchMock));
  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
  });

  it("has no axe violations on the initial form", async () => {
    const { container } = render(<App />);
    await expectNoAxeViolations(container);
  });

  it("has no axe violations while validation errors are displayed", async () => {
    const user = userEvent.setup();
    const { container } = render(<App />);
    await user.click(screen.getByRole("button", { name: /submit claim/i }));
    expect(screen.getAllByRole("alert").length).toBeGreaterThan(0);
    await expectNoAxeViolations(container);
  });

  it("has no axe violations on the confirmation screen", async () => {
    const { container } = render(<Confirmation receipt={receipt} />);
    await expectNoAxeViolations(container);
  });
});
