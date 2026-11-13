# Remove fully paid new-payment list reports

`PaidPaymentTrackingCleanupWorker` scans the configured `Telegram__NewPaymentsChatId`
every 30 seconds. It removes both the report and its tracked product photo for
an issued batch/list when at least one associated order exists and **each**
associated order has exactly one nondeleted invoice with status `Paid`. Deleted
orders, missing/duplicate invoices, cancelled invoices and unpaid invoices block
cleanup. Empty reports and reports in another destination are never removed.

The final check and deletion run inside a serializable SQL transaction, with a
per-list application lock. Financial changes cannot race past the last check.
The worker only clears the live message IDs after both deletions are acknowledged.
Invoice, payment, customer and order-item records are retained unchanged.

Each report incarnation has its own durable `PaidPaymentTrackingCleanup` receipt,
including original target IDs, acknowledged deletion timestamps and errors.
A missing Telegram message is an acknowledged deletion. Transient failures respect
retry timing; deletion/permission restrictions are deferred and remain reviewable,
never marked as successful. Telegram can refuse deletion of older messages;
those posts require manual removal using their retained links.

The invoice refresh flow republishes an unpaid report when a payment is reversed
after cleanup. Fully paid reports are not republished by a later report-send call.
The re-created report has new message IDs and an independent audit receipt when
it is paid and cleaned again.

Validation: `ZibasheERP.API.Tests --paid-tracking-cleanup`, using only the isolated
`ZibasheERPCloudDev` SQL database. No schema migration or new menu access is needed.
