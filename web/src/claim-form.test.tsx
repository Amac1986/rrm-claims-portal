import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { App } from "./App";
import { dateWindow } from "./schema";

const { max: today } = dateWindow();

const valid = {
  policyNumber: "HO-4471902",
  firstName: "Dana",
  lastName: "Okonjo",
  email: "dana@example.ca",
  phone: "204-555-0100",
  incidentDate: today,
  lossType: "Water",
  description: "A pipe burst in the basement and flooded it.",
};

type User = ReturnType<typeof userEvent.setup>;

const fillers: Record<string, (user: User) => Promise<void>> = {
  policyNumber: (user) => user.type(screen.getByLabelText(/policy number/i), valid.policyNumber),
  firstName: (user) => user.type(screen.getByLabelText(/first name/i), valid.firstName),
  lastName: (user) => user.type(screen.getByLabelText(/last name/i), valid.lastName),
  email: (user) => user.type(screen.getByLabelText(/^email/i), valid.email),
  phone: (user) => user.type(screen.getByLabelText(/^phone/i), valid.phone),
  incidentDate: (user) => user.type(screen.getByLabelText(/date of loss/i), valid.incidentDate),
  lossType: (user) => user.selectOptions(screen.getByLabelText(/type of loss/i), valid.lossType),
  description: (user) => user.type(screen.getByLabelText(/what happened/i), valid.description),
  addressLine1: (user) => user.type(screen.getByLabelText(/address line 1/i), "1 Main St"),
  city: (user) => user.type(screen.getByLabelText(/city/i), "Winnipeg"),
  province: (user) => user.selectOptions(screen.getByLabelText(/province/i), "MB"),
  postalCode: (user) => user.type(screen.getByLabelText(/postal code/i), "r3c1a1"),
};

async function fillForm(user: User, skip: string[] = []) {
  for (const [key, fill] of Object.entries(fillers)) {
    if (!skip.includes(key)) await fill(user);
  }
}

const requiredLabels = [
  /policy number/i,
  /first name/i,
  /last name/i,
  /^email/i,
  /^phone/i,
  /date of loss/i,
  /type of loss/i,
  /what happened/i,
  /address line 1/i,
  /^city/i,
  /province/i,
  /postal code/i,
];

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const receipt = {
  claimId: "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  claimNumber: "CLM-20261026-K7F3QX",
  submittedAt: "2026-10-26T14:03:11.482Z",
  status: "Received",
};

describe("claim form accessibility and error presentation", () => {
  const fetchMock = vi.fn();
  beforeEach(() => vi.stubGlobal("fetch", fetchMock));
  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
  });

  it("exposes required semantics for every required control and not for optional ones", () => {
    render(<App />);
    for (const label of requiredLabels) {
      expect(screen.getByLabelText(label)).toBeRequired();
    }
    expect(screen.getByLabelText(/address line 2/i)).not.toBeRequired();
    expect(screen.getByLabelText(/estimated loss amount/i)).not.toBeRequired();
  });

  it("marks every required field invalid with an alert-linked message on an empty submit", async () => {
    const user = userEvent.setup();
    render(<App />);
    await user.click(screen.getByRole("button", { name: /submit claim/i }));

    for (const label of requiredLabels) {
      const control = screen.getByLabelText(label);
      expect(control).toHaveAttribute("aria-invalid", "true");
      const describedBy = control.getAttribute("aria-describedby")?.split(" ") ?? [];
      const errorId = describedBy.find((id) => id.endsWith("-error"));
      expect(errorId, `${label} should reference an error message`).toBeTruthy();
      expect(document.getElementById(errorId!)).toHaveAttribute("role", "alert");
    }
    expect(screen.getByRole("status")).toHaveTextContent(/12 errors/i);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("focuses the first invalid control when the whole form is empty", async () => {
    const user = userEvent.setup();
    render(<App />);
    await user.click(screen.getByRole("button", { name: /submit claim/i }));
    await waitFor(() => expect(screen.getByLabelText(/policy number/i)).toHaveFocus());
  });

  it("focuses the first invalid control, not merely the first field", async () => {
    const user = userEvent.setup();
    render(<App />);
    await fillForm(user, ["lastName"]);
    await user.click(screen.getByRole("button", { name: /submit claim/i }));
    await waitFor(() => expect(screen.getByLabelText(/last name/i)).toHaveFocus());
  });

  it("focuses the first server-rejected field, including nested contract keys", async () => {
    fetchMock.mockResolvedValue(
      json(400, {
        status: 400,
        title: "One or more validation errors occurred.",
        errors: { "propertyAddress.postalCode": ["Postal code not recognised."] },
      }),
    );
    const user = userEvent.setup();
    render(<App />);
    await fillForm(user);
    await user.click(screen.getByRole("button", { name: /submit claim/i }));

    const postal = screen.getByLabelText(/postal code/i);
    await waitFor(() => expect(postal).toHaveFocus());
    expect(postal).toHaveAttribute("aria-invalid", "true");
    expect(screen.getByText(/postal code not recognised/i)).toBeInTheDocument();
  });

  it("focuses the earliest form field when the API reports multiple errors out of form order", async () => {
    fetchMock.mockResolvedValue(
      json(400, {
        status: 400,
        title: "One or more validation errors occurred.",
        errors: {
          "propertyAddress.postalCode": ["Postal code not recognised."],
          email: ["'email' is not a valid email address."],
        },
      }),
    );
    const user = userEvent.setup();
    render(<App />);
    await fillForm(user);
    await user.click(screen.getByRole("button", { name: /submit claim/i }));

    await waitFor(() => expect(screen.getByLabelText(/^email/i)).toHaveFocus());
  });

  it("confirms with the claim number returned by the API and moves focus to the heading", async () => {
    fetchMock.mockResolvedValue(json(201, receipt));
    const user = userEvent.setup();
    render(<App />);
    await fillForm(user);
    await user.click(screen.getByRole("button", { name: /submit claim/i }));

    expect(await screen.findByTestId("claim-number")).toHaveTextContent(receipt.claimNumber);
    expect(screen.getByText(/check your email/i)).toBeInTheDocument();
    expect(screen.getByRole("heading", { level: 1 })).toHaveFocus();
  });

  it("posts the exact contract payload, including a numeric amount when supplied", async () => {
    fetchMock.mockResolvedValue(json(201, receipt));
    const user = userEvent.setup();
    render(<App />);
    await fillForm(user);
    await user.type(screen.getByLabelText(/estimated loss amount/i), "1234.56");
    await user.type(screen.getByLabelText(/address line 2/i), "Apt 2");
    await user.click(screen.getByRole("button", { name: /submit claim/i }));

    await screen.findByTestId("claim-number");
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toMatch(/\/api\/claims$/);
    expect(init.method).toBe("POST");
    expect(init.headers).toMatchObject({ "Content-Type": "application/json" });
    const body = JSON.parse(init.body);
    expect(body).toMatchObject({
      ...valid,
      estimatedLossAmount: 1234.56,
      propertyAddress: {
        addressLine1: "1 Main St",
        addressLine2: "Apt 2",
        city: "Winnipeg",
        province: "MB",
        postalCode: "R3C 1A1",
      },
    });
    expect(typeof body.estimatedLossAmount).toBe("number");
  });

  it("keeps the form usable and explains a network failure", async () => {
    fetchMock.mockRejectedValue(new TypeError("network down"));
    const user = userEvent.setup();
    render(<App />);
    await fillForm(user);
    await user.click(screen.getByRole("button", { name: /submit claim/i }));

    expect(await screen.findByText(/something went wrong on our side/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /submit claim/i })).toBeEnabled();
    expect(screen.queryByTestId("claim-number")).not.toBeInTheDocument();
  });
});
