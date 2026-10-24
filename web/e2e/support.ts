import { expect, type Page } from "@playwright/test";

/** A value unique to this test run, used to key claims, addresses and Mailpit searches. */
export function uniqueRunId(): string {
  return `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
}

/** Local calendar YYYY-MM-DD, matching the form's date input and the API's date contract. */
export function isoDate(daysAgo: number): string {
  const d = new Date();
  d.setDate(d.getDate() - daysAgo);
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

export interface ClaimInput {
  runId: string;
  email: string;
  policyNumber: string;
  incidentDate: string;
  description: string;
}

export function claimInput(runId: string): ClaimInput {
  return {
    runId,
    email: `e2e+${runId}@example.ca`,
    policyNumber: `HO-E2E-${runId}`,
    incidentDate: isoDate(2),
    // Clearly identifies the run in the email body and is comfortably over the 20-char minimum.
    description: `E2E claim ${runId}: a pipe burst in the basement and flooded the finished floor.`,
  };
}

/** Fill every required field on the claim form with valid data. */
export async function fillValidClaim(page: Page, claim: ClaimInput): Promise<void> {
  await page.getByLabel("Policy number").fill(claim.policyNumber);
  await page.getByLabel("First name").fill("Erin");
  await page.getByLabel("Last name").fill("Tester");
  await page.getByLabel("Email").fill(claim.email);
  await page.getByLabel("Phone").fill("204-555-0100");
  await page.getByLabel("Date of loss").fill(claim.incidentDate);
  await page.getByLabel("Type of loss").selectOption("Water");
  await page.getByLabel("What happened?").fill(claim.description);
  await page.getByLabel(/Estimated loss amount/).fill("12500");
  await page.getByLabel("Address line 1").fill("12 Main Street");
  await page.getByLabel("City").fill("Winnipeg");
  await page.getByLabel("Province or territory").selectOption("MB");
  await page.getByLabel("Postal code").fill("R3C 1A1");
}

/** Assert a field is marked invalid and is described by the expected error message. */
export async function expectFieldError(page: Page, label: string, message: string): Promise<void> {
  const field = page.getByLabel(label);
  await expect(field).toHaveAttribute("aria-invalid", "true");
  const error = page.getByRole("alert").filter({ hasText: message });
  await expect(error).toBeVisible();
  const errorId = await error.getAttribute("id");
  expect(errorId).toBeTruthy();
  await expect(field).toHaveAttribute("aria-describedby", new RegExp(`\\b${errorId}\\b`));
}
