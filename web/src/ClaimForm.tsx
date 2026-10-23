import { useState } from "react";
import { useForm, type FieldPath } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { Field } from "./Field";
import { claimSchema, dateWindow, toRequest, LOSS_TYPES, PROVINCES, type ClaimFormValues } from "./schema";
import { submitClaim, ValidationError, type ClaimReceipt } from "./api";

const LOSS_LABELS: Record<(typeof LOSS_TYPES)[number], string> = {
  Fire: "Fire", Water: "Water damage", Wind: "Wind", Hail: "Hail", Theft: "Theft", Vandalism: "Vandalism", Other: "Other",
};

const FIELD_KEYS = new Set<string>([
  "policyNumber", "firstName", "lastName", "email", "phone", "incidentDate", "lossType", "description",
  "estimatedLossAmount", "propertyAddress.addressLine1", "propertyAddress.addressLine2", "propertyAddress.city",
  "propertyAddress.province", "propertyAddress.postalCode",
]);

function countErrors(o: object): number {
  let n = 0;
  for (const v of Object.values(o)) {
    if (v && typeof v === "object") n += "message" in v ? 1 : "ref" in v ? 0 : countErrors(v);
  }
  return n;
}

export function ClaimForm({ onSuccess }: { onSuccess: (r: ClaimReceipt) => void }) {
  const {
    register, handleSubmit, setError, setFocus,
    formState: { errors: e, isSubmitting, submitCount },
  } = useForm<ClaimFormValues>({
    resolver: zodResolver(claimSchema),
    // Validate on blur, then re-validate on change once touched: no scolding mid-typing.
    mode: "onTouched",
    defaultValues: { lossType: "" as never, propertyAddress: { province: "" as never } },
  });
  const [formError, setFormError] = useState<string | null>(null);
  const { min, max } = dateWindow();

  // handleSubmit only invokes this when client validation passed; isSubmitting stays true until the
  // promise settles, which is what disables the button (double-submit guard).
  const onSubmit = handleSubmit(async (values) => {
    setFormError(null);
    try {
      onSuccess(await submitClaim(toRequest(claimSchema.parse(values))));
    } catch (err) {
      if (err instanceof ValidationError) {
        // Server keys are camelCase paths matching our field names exactly (contract section 1).
        const unmapped: string[] = [];
        let first: string | null = null;
        for (const [key, msgs] of Object.entries(err.errors)) {
          if (!FIELD_KEYS.has(key)) { unmapped.push(...msgs); continue; }
          setError(key as FieldPath<ClaimFormValues>, { type: "server", message: msgs.join(" ") });
          first ??= key;
        }
        if (first) setFocus(first as FieldPath<ClaimFormValues>);
        setFormError(
          unmapped.length ? unmapped.join(" ")
            : first ? "Please correct the highlighted fields and submit again."
            : "We could not accept the claim. Please check your details.",
        );
      } else {
        setFormError("Something went wrong on our side and your claim was not submitted. Please try again in a moment.");
      }
    }
  });

  const errorCount = countErrors(e);
  const status = isSubmitting
    ? "Submitting your claim…"
    : submitCount === 0 ? ""
    : formError ?? (errorCount ? `The form has ${errorCount} error${errorCount > 1 ? "s" : ""}. Please correct them and submit again.` : "");

  return (
    <form onSubmit={onSubmit} noValidate aria-labelledby="form-title">
      <h1 id="form-title">Submit a home insurance claim</h1>
      <p>Fields marked * are required.</p>

      {/* Always-mounted polite live region: announces submit progress and outcome. */}
      <div role="status" aria-live="polite" className={status && !isSubmitting ? "summary" : "visually-hidden"}>
        {status}
      </div>

      <fieldset>
        <legend>Policy and contact</legend>
        <Field id="policyNumber" label="Policy number" error={e.policyNumber?.message}>
          {(a) => <input {...a} autoComplete="off" {...register("policyNumber")} />}
        </Field>
        <Field id="firstName" label="First name" error={e.firstName?.message}>
          {(a) => <input {...a} autoComplete="given-name" {...register("firstName")} />}
        </Field>
        <Field id="lastName" label="Last name" error={e.lastName?.message}>
          {(a) => <input {...a} autoComplete="family-name" {...register("lastName")} />}
        </Field>
        <Field id="email" label="Email" error={e.email?.message} hint="Your confirmation will be sent here.">
          {(a) => <input {...a} type="email" autoComplete="email" {...register("email")} />}
        </Field>
        <Field id="phone" label="Phone" error={e.phone?.message}>
          {(a) => <input {...a} type="tel" autoComplete="tel" {...register("phone")} />}
        </Field>
      </fieldset>

      <fieldset>
        <legend>About the loss</legend>
        <Field id="incidentDate" label="Date of loss" error={e.incidentDate?.message} hint="Within the last 365 days.">
          {(a) => <input {...a} type="date" min={min} max={max} {...register("incidentDate")} />}
        </Field>
        <Field id="lossType" label="Type of loss" error={e.lossType?.message}>
          {(a) => (
            <select {...a} {...register("lossType")}>
              <option value="">Select…</option>
              {LOSS_TYPES.map((t) => <option key={t} value={t}>{LOSS_LABELS[t]}</option>)}
            </select>
          )}
        </Field>
        <Field id="description" label="What happened?" error={e.description?.message} hint="20 to 2000 characters.">
          {(a) => <textarea {...a} rows={6} {...register("description")} />}
        </Field>
        <Field id="estimatedLossAmount" label="Estimated loss amount (CAD)" optional error={e.estimatedLossAmount?.message}>
          {(a) => <input {...a} inputMode="decimal" {...register("estimatedLossAmount")} />}
        </Field>
      </fieldset>

      <fieldset>
        <legend>Property address</legend>
        <Field id="propertyAddress.addressLine1" label="Address line 1" error={e.propertyAddress?.addressLine1?.message}>
          {(a) => <input {...a} autoComplete="address-line1" {...register("propertyAddress.addressLine1")} />}
        </Field>
        <Field id="propertyAddress.addressLine2" label="Address line 2" optional error={e.propertyAddress?.addressLine2?.message}>
          {(a) => <input {...a} autoComplete="address-line2" {...register("propertyAddress.addressLine2")} />}
        </Field>
        <Field id="propertyAddress.city" label="City" error={e.propertyAddress?.city?.message}>
          {(a) => <input {...a} autoComplete="address-level2" {...register("propertyAddress.city")} />}
        </Field>
        <Field id="propertyAddress.province" label="Province or territory" error={e.propertyAddress?.province?.message}>
          {(a) => (
            <select {...a} autoComplete="address-level1" {...register("propertyAddress.province")}>
              <option value="">Select…</option>
              {PROVINCES.map((p) => <option key={p} value={p}>{p}</option>)}
            </select>
          )}
        </Field>
        <Field id="propertyAddress.postalCode" label="Postal code" error={e.propertyAddress?.postalCode?.message} hint="For example, R3C 1A1.">
          {(a) => <input {...a} autoComplete="postal-code" {...register("propertyAddress.postalCode")} />}
        </Field>
      </fieldset>

      <button type="submit" disabled={isSubmitting}>{isSubmitting ? "Submitting…" : "Submit claim"}</button>
    </form>
  );
}
