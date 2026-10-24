import { expect, test } from "@playwright/test";
import { waitForEmail } from "./mailpit";
import { claimInput, fillValidClaim, uniqueRunId } from "./support";

const CLAIM_NUMBER = /^CLM-\d{8}-[0-9A-HJKMNP-TV-Z]{6}$/;

/**
 * The primary journey, and the reason this suite exists:
 *
 *   browser form -> API -> outbox -> RabbitMQ -> worker -> SMTP -> Mailpit
 *
 * The final assertion reads the delivered email through Mailpit's HTTP API and checks the
 * body contains the same claim number the confirmation screen displayed. A UI-only test
 * would pass even if the message broker or worker were broken.
 */
test("submitting the form shows a claim number and delivers that number by email", async ({ page }) => {
  const runId = uniqueRunId();
  const claim = claimInput(runId);

  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Submit a home insurance claim" })).toBeVisible();

  await fillValidClaim(page, claim);
  await page.getByRole("button", { name: "Submit claim" }).click();

  // Confirmation screen: the form is replaced and the claim number is displayed.
  await expect(page.getByRole("heading", { name: "Your claim has been submitted" })).toBeVisible();
  const claimNumber = (await page.getByTestId("claim-number").textContent())?.trim() ?? "";
  expect(claimNumber).toMatch(CLAIM_NUMBER);

  // The whole point: the email for this claim actually arrived, with the same number in it.
  const email = await waitForEmail(claimNumber, { timeoutMs: 45_000 });

  expect(email.Subject).toContain(claimNumber);
  expect(email.To ?? []).toContainEqual(
    expect.objectContaining({ Address: claim.email.toLowerCase() }),
  );
  expect(email.Text).toContain(claimNumber);
  expect(email.HTML).toContain(claimNumber);
  expect(email.Text).toContain(claim.policyNumber);
});
