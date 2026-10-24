import { describe, expect, it } from "vitest";
import {
  claimSchema,
  dateWindow,
  isoDate,
  toRequest,
  LOSS_TYPES,
  PROVINCES,
  type ClaimFormValues,
} from "./schema";

const { max: today, min: oldest } = dateWindow();
const dayOffset = (days: number) => {
  const d = new Date();
  return isoDate(new Date(d.getFullYear(), d.getMonth(), d.getDate() + days));
};

const valid: ClaimFormValues = {
  policyNumber: "HO-4471902",
  firstName: "Dana",
  lastName: "Okonjo",
  email: "dana@example.ca",
  phone: "204-555-0100",
  incidentDate: today,
  lossType: "Water",
  description: "A pipe burst in the basement and flooded it.",
  estimatedLossAmount: "",
  propertyAddress: {
    addressLine1: "1 Main St",
    addressLine2: "",
    city: "Winnipeg",
    province: "MB",
    postalCode: "R3C 1A1",
  },
};

const address = (patch: Partial<ClaimFormValues["propertyAddress"]>) => ({
  ...valid.propertyAddress,
  ...patch,
});

const errorPaths = (patch: Partial<ClaimFormValues>) => {
  const result = claimSchema.safeParse({ ...valid, ...patch });
  return result.success ? [] : result.error.issues.map((issue) => issue.path.join("."));
};

const messagesFor = (patch: Partial<ClaimFormValues>) => {
  const result = claimSchema.safeParse({ ...valid, ...patch });
  return result.success ? [] : result.error.issues.map((issue) => issue.message);
};

describe("contract conformance: required fields (docs/CONTRACT.md §1)", () => {
  it.each([
    ["policyNumber", "", "policyNumber"],
    ["firstName", "", "firstName"],
    ["lastName", "", "lastName"],
    ["email", "", "email"],
    ["phone", "", "phone"],
    ["incidentDate", "", "incidentDate"],
    ["lossType", "", "lossType"],
    ["description", "", "description"],
  ] as const)("rejects a blank %s", (_field, value, path) => {
    expect(errorPaths({ [path]: value } as Partial<ClaimFormValues>)).toContain(path);
  });

  it.each([
    ["addressLine1", "propertyAddress.addressLine1"],
    ["city", "propertyAddress.city"],
    ["province", "propertyAddress.province"],
    ["postalCode", "propertyAddress.postalCode"],
  ] as const)("rejects a blank %s", (field, path) => {
    expect(errorPaths({ propertyAddress: address({ [field]: "" }) })).toContain(path);
  });

  it("rejects whitespace-only required text fields", () => {
    expect(
      errorPaths({
        policyNumber: "   ",
        firstName: "   ",
        lastName: "   ",
        description: "                    ",
        propertyAddress: address({ addressLine1: "   ", city: "   " }),
      }),
    ).toEqual(
      expect.arrayContaining([
        "policyNumber",
        "firstName",
        "lastName",
        "description",
        "propertyAddress.addressLine1",
        "propertyAddress.city",
      ]),
    );
  });

  it("accepts omitted optional fields", () => {
    const { estimatedLossAmount: _amount, ...withoutAmount } = valid;
    const optionalAddress = { ...address({}), addressLine2: undefined } as ClaimFormValues["propertyAddress"];
    expect(claimSchema.safeParse({ ...withoutAmount, propertyAddress: optionalAddress }).success).toBe(true);
  });
});

describe("contract conformance: postal code", () => {
  it.each(["R3C 1A1", "R3C1A1", "r3c 1a1", "r3c1a1"])("accepts %s", (postalCode) => {
    expect(errorPaths({ propertyAddress: address({ postalCode }) })).toEqual([]);
  });

  it.each(["12345", "A1A1A", "A1A 1A", "D1A 1A1", "A1A-1A1", "A1A  1A1"])("rejects %s", (postalCode) => {
    expect(errorPaths({ propertyAddress: address({ postalCode }) })).toContain("propertyAddress.postalCode");
  });

  it("normalises case and spacing in the request payload", () => {
    const parsed = claimSchema.parse({ ...valid, propertyAddress: address({ postalCode: "r3c1a1" }) });
    expect(toRequest(parsed).propertyAddress.postalCode).toBe("R3C 1A1");
  });
});

describe("contract conformance: phone", () => {
  it.each(["2045550100", "204-555-0100", "(204) 555-0100", "+1 204 555 0100"])("accepts %s", (phone) => {
    expect(errorPaths({ phone })).toEqual([]);
  });

  it.each(["123456789", "1".repeat(21), "204-555-ABCD", "204.555.0100", "+1/204/555"])("rejects %s", (phone) => {
    expect(errorPaths({ phone })).toContain("phone");
  });

  it.each(["204 555\t0100", "204 555\n0100"])("rejects non-space whitespace in the phone number", (phone) => {
    expect(errorPaths({ phone })).toContain("phone");
  });
});

describe("contract conformance: date of loss", () => {
  it("accepts today and the oldest permitted date", () => {
    expect(errorPaths({ incidentDate: today })).toEqual([]);
    expect(errorPaths({ incidentDate: oldest })).toEqual([]);
  });

  it("rejects a future date and a date more than 365 days ago", () => {
    expect(errorPaths({ incidentDate: dayOffset(1) })).toContain("incidentDate");
    expect(errorPaths({ incidentDate: dayOffset(-366) })).toContain("incidentDate");
  });

  it.each(["not-a-date", "2026-1-1", "2026/10/26", "2026-00-10", "2026-13-01", "2026-02-30", "2025-02-29"])(
    "rejects the invalid calendar date %s",
    (incidentDate) => {
      expect(errorPaths({ incidentDate })).toContain("incidentDate");
      expect(messagesFor({ incidentDate })).toContain("Enter a valid date.");
    },
  );

  it("does not flag a real leap day as a malformed date", () => {
    expect(messagesFor({ incidentDate: "2024-02-29" })).not.toContain("Enter a valid date.");
  });
});

describe("contract conformance: enums, lengths and amount", () => {
  it("accepts every contract lossType literal", () => {
    for (const lossType of LOSS_TYPES) expect(errorPaths({ lossType })).toEqual([]);
  });

  it("accepts every contract province literal", () => {
    for (const province of PROVINCES) {
      expect(errorPaths({ propertyAddress: address({ province }) })).toEqual([]);
    }
  });

  it.each(["Flood", "Water damage", "water", ""])("rejects the non-contract lossType %s", (lossType) => {
    expect(errorPaths({ lossType: lossType as ClaimFormValues["lossType"] })).toContain("lossType");
  });

  it("enforces the 20–2000 character description window", () => {
    expect(errorPaths({ description: "x".repeat(20) })).toEqual([]);
    expect(errorPaths({ description: "x".repeat(2000) })).toEqual([]);
    expect(errorPaths({ description: "x".repeat(19) })).toContain("description");
    expect(errorPaths({ description: "x".repeat(2001) })).toContain("description");
  });

  it.each(["0", "0.00", "999.99", "10000000", "10000000.00"])("accepts the amount %s", (estimatedLossAmount) => {
    expect(errorPaths({ estimatedLossAmount })).toEqual([]);
  });

  it.each(["-1", "10000000.01", "1,000", "1e3", "twelve"])("rejects the amount %s", (estimatedLossAmount) => {
    expect(errorPaths({ estimatedLossAmount })).toContain("estimatedLossAmount");
  });
});

describe("contract conformance: request payload", () => {
  it("sends contract field names, a literal lossType and a numeric amount", () => {
    const parsed = claimSchema.parse({
      ...valid,
      lossType: "Theft",
      estimatedLossAmount: "1234.56",
      propertyAddress: address({ postalCode: "r3c1a1", addressLine2: "Apt 2" }),
    });
    expect(toRequest(parsed)).toEqual({
      policyNumber: "HO-4471902",
      firstName: "Dana",
      lastName: "Okonjo",
      email: "dana@example.ca",
      phone: "204-555-0100",
      incidentDate: today,
      lossType: "Theft",
      description: "A pipe burst in the basement and flooded it.",
      estimatedLossAmount: 1234.56,
      propertyAddress: {
        addressLine1: "1 Main St",
        addressLine2: "Apt 2",
        city: "Winnipeg",
        province: "MB",
        postalCode: "R3C 1A1",
      },
    });
    expect(typeof toRequest(parsed).estimatedLossAmount).toBe("number");
  });

  it("drops empty optional fields rather than sending nulls or empty strings", () => {
    const parsed = claimSchema.parse({
      ...valid,
      estimatedLossAmount: "   ",
      propertyAddress: address({ addressLine2: "   " }),
    });
    const wire = JSON.parse(JSON.stringify(toRequest(parsed)));
    expect(wire).not.toHaveProperty("estimatedLossAmount");
    expect(wire.propertyAddress).not.toHaveProperty("addressLine2");
  });
});
