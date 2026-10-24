/**
 * Minimal Mailpit HTTP API client for asserting on the confirmation email.
 *
 * The notification worker is asynchronous, so every read here polls with a bounded
 * timeout and a short interval instead of sleeping for a fixed duration.
 */

export const MAILPIT_URL = (process.env.MAILPIT_URL ?? "http://localhost:8025").replace(/\/$/, "");

interface MailpitAddress {
  Name: string;
  Address: string;
}

export interface MailpitMessageSummary {
  ID: string;
  Subject: string;
  To: MailpitAddress[] | null;
  Created: string;
}

export interface MailpitMessage extends MailpitMessageSummary {
  Text: string;
  HTML: string;
}

interface MailpitSearchResponse {
  messages: MailpitMessageSummary[] | null;
}

export interface WaitForEmailOptions {
  /** Fail after this many milliseconds. Defaults to 45s. */
  timeoutMs?: number;
  /** How often to ask Mailpit for new mail. Defaults to 500ms. */
  intervalMs?: number;
}

export class MailpitError extends Error {}

async function getJson<T>(path: string): Promise<T> {
  let res: Response;
  try {
    res = await fetch(`${MAILPIT_URL}${path}`);
  } catch (err) {
    throw new MailpitError(
      `Mailpit is not reachable at ${MAILPIT_URL}. Is the compose stack up? (${String(err)})`,
    );
  }
  if (!res.ok) {
    throw new MailpitError(`Mailpit ${path} returned HTTP ${res.status}.`);
  }
  return (await res.json()) as T;
}

/** Search matches the claim number in the subject and body. */
export async function searchMessages(query: string): Promise<MailpitMessageSummary[]> {
  const body = await getJson<MailpitSearchResponse>(
    `/api/v1/search?query=${encodeURIComponent(query)}`,
  );
  return body.messages ?? [];
}

export async function getMessage(id: string): Promise<MailpitMessage> {
  return getJson<MailpitMessage>(`/api/v1/message/${encodeURIComponent(id)}`);
}

/**
 * Poll Mailpit until at least one message matches `query`, then return the full message.
 * The returned email is the first match, so callers should use a value unique to the claim
 * (the claim number) rather than a shared address.
 */
export async function waitForEmail(
  query: string,
  { timeoutMs = 45_000, intervalMs = 500 }: WaitForEmailOptions = {},
): Promise<MailpitMessage> {
  const deadline = Date.now() + timeoutMs;
  let lastCount = 0;
  let lastError: unknown;

  while (Date.now() < deadline) {
    try {
      const messages = await searchMessages(query);
      lastCount = messages.length;
      if (messages.length > 0) {
        return await getMessage(messages[0].ID);
      }
    } catch (err) {
      lastError = err;
    }
    await new Promise((resolve) => setTimeout(resolve, intervalMs));
  }

  const detail = lastError instanceof Error ? ` Last error: ${lastError.message}` : "";
  throw new Error(
    `No Mailpit message matching "${query}" after ${timeoutMs}ms (last match count: ${lastCount}).${detail}`,
  );
}
