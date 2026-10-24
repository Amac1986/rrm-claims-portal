import { expect, test } from "@playwright/test";
import { searchMessages } from "./mailpit";
import { claimInput, expectFieldError, uniqueRunId } from "./support";

/**
 * A bad form must be rejected in the browser before it reaches the API: errors appear on
 * the offending fields and no claim is persisted or emailed.
 */
test("invalid submission shows field errors and persists nothing", async ({ page }) => {
  const runId = uniqueRunId();
  const claim = claimInput(runId);

  let claimPosts = 0;
  page.on("request", (request) => {
    if (request.method() === "POST" && new URL(request.url()).pathname === "/api/claims") {
      claimPosts += 1;
    }
  });

  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Submit a home insurance claim" })).toBeVisible();

  // Leave every required field empty, add a plausible (valid) address so the mail search
  // below is unambiguous if a request ever did get through.
  await page.getByLabel("Email").fill(claim.email);

  await page.getByRole("button", { name: "Submit claim" }).click();

  await expect(page.getByRole("status")).toContainText(/The form has \d+ errors?\./);

  await expectFieldError(page, "Policy number", "Policy number is required.");
  await expectFieldError(page, "First name", "First name is required.");
  await expectFieldError(page, "Last name", "Last name is required.");
  await expectFieldError(page, "Phone", "Phone number must be at least 10 characters.");
  await expectFieldError(page, "Date of loss", "Date of loss is required.");
  await expectFieldError(page, "Type of loss", "Select a type of loss.");
  await expectFieldError(
    page,
    "What happened?",
    "Description must be at least 20 characters.",
  );
  await expectFieldError(page, "Address line 1", "Address is required.");
  await expectFieldError(page, "City", "City is required.");
  await expectFieldError(page, "Province or territory", "Select a province or territory.");
  await expectFieldError(page, "Postal code", "Enter a postal code like A1A 1A1.");

  // Format-specific errors on the right fields.
  await page.getByLabel("Email").fill("not-an-email");
  await page.getByLabel("Email").blur();
  await expectFieldError(page, "Email", "Enter a valid email address.");

  await page.getByLabel("Postal code").fill("ZZZ");
  await page.getByLabel("Postal code").blur();
  await expectFieldError(page, "Postal code", "Enter a postal code like A1A 1A1.");

  // Still on the form, not on the confirmation screen.
  await expect(page.getByRole("heading", { name: "Your claim has been submitted" })).toHaveCount(0);

  // Client validation blocked the submission, so nothing could have been persisted or emailed.
  expect(claimPosts).toBe(0);
  expect(await searchMessages(claim.email)).toHaveLength(0);
});
