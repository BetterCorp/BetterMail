# Draft lifecycle

A new composer persists a draft when it contains authored content. A default signature, empty HTML editor blocks, and the original recipients, subject, quote and attachments of an untouched reply or forward do not count. Explicit recipient, subject, body, attachment and message-option changes do count. Sending explicitly persists even an otherwise untouched response before queueing it.

Returning an autosaved composer to its defaults deletes that draft through the normal durable deletion queue. Further typing uses a new local identity because the discarded identity has a tombstone. Closing waits for persistence or deletion; a failure leaves the composer open. Opening and closing an existing draft and redundant saves do not advance its content revision.

Cloud reconciliation fetches and validates every page of the mailbox's Drafts snapshot before changing local data. It matches the current regular ID or the captured immutable ID. When a previously synchronized copy is absent, the server has removed it from Drafts (by sending, deleting or moving it). The unchanged local mirror is then removed transactionally, with the same revision, owner, identities and nonqueued state checked again. A tombstone prevents a late autosave or stale server snapshot from resurrecting it. This also applies to legacy drafts whose old IDs no longer resolve.

Unsynced local changes are retained with an explicit reconciliation issue. If a clean draft is removed while its composer is open, subsequent edits are saved under a fresh local identity, without carrying the removed provider mapping. The store acknowledges whether a save was accepted; an ignored save cannot silently close the composer. Queued messages cannot take this recovery path.

An incomplete or failed server listing never authorizes removal. Draft disappearance is not evidence of delivery: queued sends remain under the outbox's separate acceptance and uncertain-send recovery rules.
