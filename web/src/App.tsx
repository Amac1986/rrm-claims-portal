import { useState } from "react";
import { ClaimForm } from "./ClaimForm";
import { Confirmation } from "./Confirmation";
import type { ClaimReceipt } from "./api";

export function App() {
  const [receipt, setReceipt] = useState<ClaimReceipt | null>(null);
  return (
    <>
      <a className="skip" href="#main">Skip to content</a>
      <header><strong>Red River Mutual</strong></header>
      <main id="main">{receipt ? <Confirmation receipt={receipt} /> : <ClaimForm onSuccess={setReceipt} />}</main>
    </>
  );
}
