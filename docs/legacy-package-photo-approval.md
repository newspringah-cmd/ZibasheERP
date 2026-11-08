# Photo approval for legacy stock packages

An installed preparation-group address with no ERP item snapshot and no ERP shipping request can start the existing 1–3 photo / Yes–No / customer confirmation workflow. This supports decants from the previous system without creating artificial invoices, orders or shipping items.

The photo-only scope must bind an existing non-deleted address to its actual customer and an installed root address message in the configured preparation group. Operator authorization, exact message binding, active accounting-group destination and customer identity checks remain required at every relevant stage. A spent ordinary item snapshot cannot silently become a legacy package.

Receipts add optional LegacyAddressOnly, AddressId and AddressMessageId fields, with a distinct per-address scope. Sending, confirmation, correction text and cleanup use the normal durable delivery machinery, cooldown handling, receipt IDs and Yes/No form. Operator notices reply to the exact address and omit a misleading zero-item count. Unknown delivery outcomes remain review-only.

Legacy photo-only confirmations are explicitly excluded from ERP dispatch approval and dispatch-state mutation. Existing invoice-backed parcels keep their item membership and recipient validation. No schema migration is needed; financial and fulfillment records are untouched.
