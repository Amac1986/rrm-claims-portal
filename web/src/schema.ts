import { z } from "zod";

// Mirrors docs/CONTRACT.md §1, §3, §4. The server stays authoritative; this gives fast feedback.
export const LOSS_TYPES = ["Fire", "Water", "Wind", "Hail", "Theft", "Vandalism", "Other"] as const;
export const PROVINCES = ["AB", "BC", "MB", "NB", "NL", "NS", "NT", "NU", "ON", "PE", "QC", "SK", "YT"] as const;

const pad = (n: number) => String(n).padStart(2, "0");
/** Local calendar date as YYYY-MM-DD. incidentDate is a date, not a timestamp (§6). */
export function isoDate(d: Date): string {
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}
export function dateWindow(now = new Date()) {
  const min = new Date(now.getFullYear(), now.getMonth(), now.getDate() - 365);
  return { min: isoDate(min), max: isoDate(now) };
}

const required = (label: string, max: number) =>
  z.string().trim().min(1, `${label} is required.`).max(max, `${label} must be ${max} characters or fewer.`);

const POSTAL = /^[ABCEGHJ-NPRSTVXY]\d[ABCEGHJ-NPRSTV-Z][ ]?\d[ABCEGHJ-NPRSTV-Z]\d$/i;
const PHONE_CHARS = /^[\d\s\-+()]+$/;

export const claimSchema = z.object({
  policyNumber: required("Policy number", 32),
  firstName: required("First name", 64),
  lastName: required("Last name", 64),
  email: z
    .string()
    .trim()
    .min(1, "Email is required.")
    .max(254, "Email must be 254 characters or fewer.")
    .pipe(z.email("Enter a valid email address.")),
  phone: z
    .string()
    .trim()
    .min(10, "Phone number must be at least 10 characters.")
    .max(20, "Phone number must be 20 characters or fewer.")
    .regex(PHONE_CHARS, "Phone number may only contain digits, spaces, + - ( )."),
  incidentDate: z
    .string()
    .min(1, "Date of loss is required.")
    .superRefine((v, ctx) => {
      const { min, max } = dateWindow();
      if (!/^\d{4}-\d{2}-\d{2}$/.test(v)) ctx.addIssue({ code: "custom", message: "Enter a valid date." });
      else if (v > max) ctx.addIssue({ code: "custom", message: "Date of loss cannot be in the future." });
      else if (v < min) ctx.addIssue({ code: "custom", message: "Date of loss cannot be more than 365 days ago." });
    }),
  lossType: z.enum(LOSS_TYPES, "Select a type of loss."),
  description: z
    .string()
    .trim()
    .min(20, "Description must be at least 20 characters.")
    .max(2000, "Description must be 2000 characters or fewer."),
  // Kept as a string in the form (empty = omitted); converted to a number in toRequest.
  estimatedLossAmount: z
    .string()
    .trim()
    .refine((v) => v === "" || (/^\d+(\.\d+)?$/.test(v) && Number(v) <= 10_000_000), {
      message: "Enter an amount between 0 and 10,000,000.",
    }),
  propertyAddress: z.object({
    addressLine1: required("Address", 128),
    addressLine2: z.string().trim().max(128, "Address line 2 must be 128 characters or fewer."),
    city: required("City", 64),
    province: z.enum(PROVINCES, "Select a province or territory."),
    postalCode: z.string().trim().regex(POSTAL, "Enter a postal code like A1A 1A1."),
  }),
});

export type ClaimFormValues = z.input<typeof claimSchema>;

export interface SubmitClaimRequest {
  policyNumber: string;
  firstName: string;
  lastName: string;
  email: string;
  phone: string;
  incidentDate: string;
  lossType: string;
  description: string;
  estimatedLossAmount?: number;
  propertyAddress: {
    addressLine1: string;
    addressLine2?: string;
    city: string;
    province: string;
    postalCode: string;
  };
}

/** Normalises postal code per §1 (uppercase, one space) and drops empty optionals. */
export function toRequest(v: z.output<typeof claimSchema>): SubmitClaimRequest {
  const pc = v.propertyAddress.postalCode.replace(/\s+/g, "").toUpperCase();
  return {
    ...v,
    estimatedLossAmount: v.estimatedLossAmount === "" ? undefined : Number(v.estimatedLossAmount),
    propertyAddress: {
      ...v.propertyAddress,
      addressLine2: v.propertyAddress.addressLine2 || undefined,
      postalCode: `${pc.slice(0, 3)} ${pc.slice(3)}`,
    },
  };
}
