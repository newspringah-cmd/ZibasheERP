Tracking confirmation commits one SQL NotificationOutbox job per matched dispatch
before returning to Telegram. The dispatch ID is the job ID; repeated confirmations
cannot create a second job. The worker leases one job at a time and uses the host
shutdown token, so closing or timing out the webhook does not stop the batch.

Jobs survive restarts using the existing five-minute outbox lease. A stale dispatch
with no uncertain delivery may resume. Successful destination receipts are skipped;
an attempted send without a persisted success is held for manual review. Never
clear these uncertain receipts automatically: Telegram photo sends have no
idempotency key.

An explicit multipart HTTP 429 is a known rejection. Only that rejected attempt is
released for retry. The job and pending tracking jobs retain the full retry_after
deadline plus a one-second margin. Other technical interruptions with no ambiguous
delivery use backoff capped at five minutes. Missing groups, unsafe recipient
matches and uncertain sends remain visible in the existing tracking report.

The worker spaces jobs by 3.1 seconds because updating their shipping status
buttons shares an admin group. No schema migration is required. Old batches are
not silently re-enqueued on deployment; new confirmations use the durable queue.

Tests use a fake Telegram transport and the isolated ZibasheERPCloudDev database.
They queue 40 items twice, deliver 24, reject number 25 with retry_after=120, then
resume using a new database context. All 40 complete with 40 accepted sends and
one rejected attempt. Separate tests verify unknown outcomes, leases, and the
exact Telegram cooldown. Set TRACKING_TEST_DB_CONNECTION only for the explicitly
guarded local development database to enable the SQL integration checks.

## Receiver matching and group review

Matching now reads complete receiver-name lines in free-form addresses, normalizes Persian/Arabic forms (including alif and hamza variants), and tolerates small OCR differences only with a unique high-confidence name. Street names and isolated surnames are not receiver evidence. Exact structured destination cities may disambiguate equal full names, but cannot rescue a weak name match. Closed parcels are excluded and multiple open parcels require review.

The latest-batch report and previews offer group rematching. This upgrades confident, unattempted review rows without sending, preserves low OCR-confidence rows and all prior/uncertain deliveries, and prevents competing tracking codes claiming one parcel. Newly ready rows still require explicit send confirmation.
