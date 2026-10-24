import type { ReactNode } from "react";

interface Props {
  id: string;
  label: string;
  error?: string;
  hint?: string;
  optional?: boolean;
  children: (aria: {
    id: string;
    "aria-invalid": boolean;
    "aria-required"?: boolean;
    "aria-describedby"?: string;
  }) => ReactNode;
}

/**
 * Label + control + hint + error wiring in one place so every field gets the same a11y treatment:
 * <label for>, aria-invalid, aria-describedby -> hint/error. The error is role="alert" so it is
 * announced when it appears, and carries a text prefix and icon so it is not colour-only.
 */
export function Field({ id, label, error, hint, optional, children }: Props) {
  const hintId = `${id}-hint`;
  const errId = `${id}-error`;
  const describedBy = [hint && hintId, error && errId].filter(Boolean).join(" ") || undefined;
  return (
    <div className={`field${error ? " field--invalid" : ""}`}>
      <label htmlFor={id}>
        {label}
        {optional ? <span className="optional"> (optional)</span> : <span aria-hidden="true"> *</span>}
      </label>
      {hint && (
        <p className="hint" id={hintId}>
          {hint}
        </p>
      )}
      {children({
        id,
        "aria-invalid": !!error,
        // The visual asterisk is aria-hidden, so expose required-ness to assistive tech explicitly.
        "aria-required": optional ? undefined : true,
        "aria-describedby": describedBy,
      })}
      {error && (
        <p className="error" id={errId} role="alert">
          <span aria-hidden="true">⚠ </span>
          <span className="visually-hidden">Error: </span>
          {error}
        </p>
      )}
    </div>
  );
}
