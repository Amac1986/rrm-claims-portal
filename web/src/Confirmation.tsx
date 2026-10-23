import { useEffect, useRef } from "react";
import type { ClaimReceipt } from "./api";

export function Confirmation({ receipt }: { receipt: ClaimReceipt }) {
  const heading = useRef<HTMLHeadingElement>(null);
  // The form unmounts on success; move focus to the new heading so keyboard/SR users land on the result.
  useEffect(() => heading.current?.focus(), []);
  return (
    <section aria-labelledby="confirm-title" className="confirmation">
      <h1 id="confirm-title" tabIndex={-1} ref={heading}>Your claim has been submitted</h1>
      <p>Your claim number is:</p>
      <p className="claim-number" data-testid="claim-number">{receipt.claimNumber}</p>
      <p>Please check your email for a confirmation message. Keep your claim number for your records.</p>
    </section>
  );
}
