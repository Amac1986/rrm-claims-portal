# Frozen Contract — v1

**Status: FROZEN as of 2026-10-23.** Owned by the Development lead.

Three workstreams build against this document in parallel: the API, the notification
consumer, and the React form. That only works if the shapes below do not move. If you
believe something here is wrong or missing, **stop and raise it** — do not edit this
file or `ClaimPortal.Contracts` unilaterally.

---

## 1. `POST /api/claims`

### Request — `application/json`

| Field | Type | Required | Rules |
| --- | --- | --- | --- |
| `policyNumber` | string | yes | 1–32 chars, trimmed, non-blank |
| `firstName` | string | yes | 1–64 chars |
| `lastName` | string | yes | 1–64 chars |
| `email` | string | yes | valid email, ≤254 chars |
| `phone` | string | yes | 10–20 chars, digits/space/`-`/`+`/`(`/`)` |
| `incidentDate` | string (`YYYY-MM-DD`) | yes | not in the future; not more than 365 days ago |
| `lossType` | string enum | yes | see §3 |
| `description` | string | yes | 20–2000 chars |
| `estimatedLossAmount` | number | no | ≥ 0, ≤ 10,000,000 when present |
| `propertyAddress` | object | yes | see below |

`propertyAddress`:

| Field | Type | Required | Rules |
| --- | --- | --- | --- |
| `addressLine1` | string | yes | 1–128 chars |
| `addressLine2` | string | no | ≤ 128 chars |
| `city` | string | yes | 1–64 chars |
| `province` | string | yes | 2-letter Canadian province/territory code, uppercase (§4) |
| `postalCode` | string | yes | Canadian format `A1A 1A1`; accept with or without the space, normalize to one space, uppercase |

### Response `201 Created`

```json
{
  "claimId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "claimNumber": "CLM-20261026-K7F3QX",
  "submittedAt": "2026-10-26T14:03:11.482Z",
  "status": "Received"
}
```

`Location` header: `/api/claims/{claimId}`.

`status` is the string `"Received"` for every successful submission in this slice.

### Response `400 Bad Request` — RFC 7807

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "email": ["'email' is not a valid email address."],
    "description": ["'description' must be at least 20 characters."]
  }
}
```

**Error keys are camelCase request field names.** Nested fields use dots:
`propertyAddress.postalCode`. The frontend maps these keys directly onto form fields,
so the casing matters.

`500` returns ProblemDetails with no `errors` member and no exception detail.

---

## 2. `ClaimSubmitted` event

Published to the broker by the API via the transactional outbox. Consumed by
`ClaimPortal.Notifications`.

```json
{
  "claimId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "claimNumber": "CLM-20261026-K7F3QX",
  "policyNumber": "HO-4471902",
  "claimantFirstName": "Dana",
  "claimantLastName": "Okonjo",
  "claimantEmail": "dana.okonjo@example.ca",
  "lossType": "Water",
  "incidentDate": "2026-10-24",
  "submittedAt": "2026-10-26T14:03:11.482Z"
}
```

**Design decision — event-carried state transfer.** The event carries everything the
consumer needs to render the confirmation email. The consumer never calls back into the
API or reads the claims table. That keeps the two services independent: the consumer
has no dependency on the API being up, and the email reflects the claim exactly as it
was submitted.

It deliberately does **not** carry `description`, `estimatedLossAmount`, or the property
address. The confirmation email does not include them, and keeping loss details out of
an email payload is the safer default.

### Message identity and idempotency

- The MassTransit `MessageId` is the outbox row id and is **stable across redeliveries**.
- The consumer treats `MessageId` as the idempotency key and records it in
  `notification_deliveries` with a unique constraint.
- Redelivery after a crash between "SMTP accepted" and "message acked" must **not** send
  a second email.

---

## 3. `lossType` values

`Fire`, `Water`, `Wind`, `Hail`, `Theft`, `Vandalism`, `Other`

Serialized as the exact PascalCase string in both the API and the event. The frontend
sends these literals; it does not send display labels.

## 4. `province` values

`AB`, `BC`, `MB`, `NB`, `NL`, `NS`, `NT`, `NU`, `ON`, `PE`, `QC`, `SK`, `YT`

## 5. Claim number format

`CLM-{yyyyMMdd}-{6 chars Crockford base32}` — e.g. `CLM-20261026-K7F3QX`.

- The date is the submission date in UTC.
- The suffix comes from a CSPRNG, excluding `I`, `L`, `O`, `U`.
- Uniqueness is enforced by a **database unique constraint** with a bounded retry, not
  by trusting the generator.
- It is not the primary key. `claimId` (GUID) is.

## 6. Conventions

- All timestamps are UTC, ISO 8601, `Z`-suffixed, serialized by `System.Text.Json`.
- `incidentDate` is a **date, not a timestamp** — `YYYY-MM-DD`, no timezone.
- JSON is camelCase on the wire. C# records use PascalCase properties.
- `Content-Type: application/json; charset=utf-8`.
- **Every property on `SubmitClaimRequest` is nullable, on purpose.** A missing
  required field must surface as a FluentValidation message under the right
  `errors` key, not as a model-binding failure with a framework-worded message the
  frontend cannot map to a field. Required-ness is a validation rule here, not a
  type constraint. `ClaimSubmitted` is the opposite — it uses `required` members,
  because by then the data is known-good.
