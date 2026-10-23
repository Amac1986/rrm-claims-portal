import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { App } from "./App";
import { claimSchema, dateWindow, isoDate } from "./schema";

const { max: today, min: oldest } = dateWindow();
const dayOffset = (days: number) => {
  const d = new Date();
  return isoDate(new Date(d.getFullYear(), d.getMonth(), d.getDate() + days));
};

const valid = {
  policyNumber: "HO-4471902",
  firstName: "Dana",
  lastName: "Okonjo",
  email: "dana@example.ca",
  phone: "204-555-0100",
  incidentDate: today,
  lossType: "Water",
  description: "A pipe burst in the basement and flooded it.",
  estimatedLossAmount: "",
  propertyAddress: { addressLine1: "1 Main St", addressLine2: "", city: "Winnipeg", province: "MB", postalCode: "r3c1a1" },
};

describe("schema rules (contract section 1)", () => {
  const errorsFor = (patch: object) => {
    const r = claimSchema.safeParse({ ...valid, ...patch });
    return r.success ? [] : r.error.issues.map((i) => i.path.join("."));
  };
  it("accepts a valid claim", () => expect(errorsFor({})).toEqual([]));
  it.each([
    ["policyNumber blank", { policyNumber: "   " }, "policyNumber"],
    ["policyNumber > 32", { policyNumber: "x".repeat(33) }, "policyNumber"],
    ["firstName > 64", { firstName: "x".repeat(65) }, "firstName"],
    ["lastName empty", { lastName: "" }, "lastName"],
    ["bad email", { email: "nope" }, "email"],
    ["phone too short", { phone: "12345" }, "phone"],
    ["phone bad chars", { phone: "204-555-abcd" }, "phone"],
    ["future date", { incidentDate: dayOffset(1) }, "incidentDate"],
    ["date > 365 days ago", { incidentDate: dayOffset(-366) }, "incidentDate"],
    ["bad lossType", { lossType: "Flood" }, "lossType"],
    ["description < 20", { description: "too short" }, "description"],
    ["description > 2000", { description: "x".repeat(2001) }, "description"],
    ["negative amount", { estimatedLossAmount: "-5" }, "estimatedLossAmount"],
    ["amount > 10M", { estimatedLossAmount: "10000001" }, "estimatedLossAmount"],
    ["bad province", { propertyAddress: { ...valid.propertyAddress, province: "ZZ" } }, "propertyAddress.province"],
    ["bad postal", { propertyAddress: { ...valid.propertyAddress, postalCode: "12345" } }, "propertyAddress.postalCode"],
  ])("rejects %s", (_n, patch, path) => expect(errorsFor(patch)).toContain(path));

  it("accepts boundary values", () => {
    expect(errorsFor({ incidentDate: oldest, description: "x".repeat(20), estimatedLossAmount: "10000000" })).toEqual([]);
    expect(errorsFor({ propertyAddress: { ...valid.propertyAddress, postalCode: "R3C 1A1" } })).toEqual([]);
  });
});

async function fillForm(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByLabelText(/policy number/i), valid.policyNumber);
  await user.type(screen.getByLabelText(/first name/i), valid.firstName);
  await user.type(screen.getByLabelText(/last name/i), valid.lastName);
  await user.type(screen.getByLabelText(/^email/i), valid.email);
  await user.type(screen.getByLabelText(/^phone/i), valid.phone);
  await user.type(screen.getByLabelText(/date of loss/i), valid.incidentDate);
  await user.selectOptions(screen.getByLabelText(/type of loss/i), "Water");
  await user.type(screen.getByLabelText(/what happened/i), valid.description);
  await user.type(screen.getByLabelText(/address line 1/i), "1 Main St");
  await user.type(screen.getByLabelText(/city/i), "Winnipeg");
  await user.selectOptions(screen.getByLabelText(/province/i), "MB");
  await user.type(screen.getByLabelText(/postal code/i), "r3c1a1");
}

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

describe("claim form", () => {
  const fetchMock = vi.fn();
  beforeEach(() => vi.stubGlobal("fetch", fetchMock));
  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
  });

  it("shows inline, linked errors and does not call the API when invalid", async () => {
    const user = userEvent.setup();
    render(<App />);
    await user.click(screen.getByRole("button", { name: /submit claim/i }));
    const email = screen.getByLabelText(/^email/i);
    expect(email).toHaveAttribute("aria-invalid", "true");
    const errId = email.getAttribute("aria-describedby")!.split(" ").pop()!;
    expect(document.getElementById(errId)).toHaveAttribute("role", "alert");
    expect(screen.getByRole("status")).toHaveTextContent(/error/i);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("submits normalised JSON and renders claim number + check-your-email", async () => {
    fetchMock.mockResolvedValue(
      json(201, { claimId: "g", claimNumber: "CLM-20261026-K7F3QX", submittedAt: "2026-10-26T14:03:11.482Z", status: "Received" }),
    );
    const user = userEvent.setup();
    render(<App />);
    await fillForm(user);
    await user.click(screen.getByRole("button", { name: /submit claim/i }));

    expect(await screen.findByTestId("claim-number")).toHaveTextContent("CLM-20261026-K7F3QX");
    expect(screen.getByText(/check your email/i)).toBeInTheDocument();
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toMatch(/\/api\/claims$/);
    const body = JSON.parse(init.body);
    expect(body.propertyAddress.postalCode).toBe("R3C 1A1");
    expect(body).not.toHaveProperty("estimatedLossAmount");
    expect(body.propertyAddress).not.toHaveProperty("addressLine2");
  });

  it("maps server 400 errors onto the right fields, including nested keys", async () => {
    fetchMock.mockResolvedValue(
      json(400, {
        status: 400,
        errors: { email: ["'email' is not a valid email address."], "propertyAddress.postalCode": ["Postal code not recognised."] },
      }),
    );
    const user = userEvent.setup();
    render(<App />);
    await fillForm(user);
    await user.click(screen.getByRole("button", { name: /submit claim/i }));

    const email = await screen.findByLabelText(/^email/i);
    await waitFor(() => expect(email).toHaveAttribute("aria-invalid", "true"));
    expect(screen.getByText(/not a valid email address/i)).toBeInTheDocument();
    const postal = screen.getByLabelText(/postal code/i);
    expect(postal).toHaveAttribute("aria-invalid", "true");
    expect(screen.getByText(/postal code not recognised/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/first name/i)).toHaveAttribute("aria-invalid", "false");
    expect(screen.queryByTestId("claim-number")).not.toBeInTheDocument();
  });

  it("shows a non-field message on 500", async () => {
    fetchMock.mockResolvedValue(json(500, { status: 500, title: "An error occurred" }));
    const user = userEvent.setup();
    render(<App />);
    await fillForm(user);
    await user.click(screen.getByRole("button", { name: /submit claim/i }));
    expect(await screen.findByText(/something went wrong on our side/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /submit claim/i })).toBeEnabled();
  });

  it("blocks double submit while a request is in flight", async () => {
    let resolve!: (r: Response) => void;
    fetchMock.mockReturnValue(new Promise<Response>((r) => (resolve = r)));
    const user = userEvent.setup();
    render(<App />);
    await fillForm(user);
    const button = screen.getByRole("button", { name: /submit claim/i });
    await user.click(button);
    const busy = await screen.findByRole("button", { name: /submitting/i });
    expect(busy).toBeDisabled();
    await user.click(busy);
    await user.keyboard("{Enter}");
    expect(fetchMock).toHaveBeenCalledTimes(1);
    resolve(json(201, { claimId: "g", claimNumber: "CLM-20261026-AAAAAA", submittedAt: "x", status: "Received" }));
    expect(await screen.findByTestId("claim-number")).toBeInTheDocument();
  });
});
