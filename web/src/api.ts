import type { SubmitClaimRequest } from "./schema";

export interface ClaimReceipt {
  claimId: string;
  claimNumber: string;
  submittedAt: string;
  status: string;
}

/** 400: errors keyed by camelCase field path, e.g. "propertyAddress.postalCode" (contract §1). */
export class ValidationError extends Error {
  constructor(public errors: Record<string, string[]>) {
    super("Validation failed");
  }
}
/** 500, network failure, or anything else with no field mapping. */
export class ServerError extends Error {}

const baseUrl = () => (import.meta.env.VITE_API_BASE_URL ?? "").replace(/\/$/, "");

export async function submitClaim(body: SubmitClaimRequest): Promise<ClaimReceipt> {
  let res: Response;
  try {
    res = await fetch(`${baseUrl()}/api/claims`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
  } catch {
    throw new ServerError("network");
  }
  if (res.status === 201) return (await res.json()) as ClaimReceipt;
  if (res.status === 400) {
    const problem = await res.json().catch(() => null);
    if (problem?.errors && typeof problem.errors === "object") throw new ValidationError(problem.errors);
  }
  throw new ServerError(String(res.status));
}
