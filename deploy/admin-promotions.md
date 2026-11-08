Administrators use `/admin` → «تبلیغات» → group/channel → numeric sales-list code.
Only the configured primary owner @zahraa_frj sees and may use the
«✉️ ارسال پیام به گروه» control in both menus. The owner submits a plain-text message, reviews
an exact preview, then explicitly confirms publication to Zibashe perfume.
Every step and the dispatch worker recheck the configured numeric owner identity; confirmation tokens are scoped to the
same chat/user, expire after twenty minutes and are invalidated by new text or
cancellation. Messages support up to 4096 UTF-16 units; attachments are not sent.
The durable job contains the approved text, and the worker sends it once with
an intent/receipt journal. Delivery is restricted to group -1001283018740 and
does not affect advertising schedules, perfume history, orders or invoices.
The administrator receives a status button and the resulting Telegram link.

Both Persian and Western digits work. Each action rechecks membership in the
configured accounting administrator group or the primary owner identity.
Changing menu or cancelling clears unfinished code input. Only open, published,
non-inventory lists with remaining volume and their own photo can be requested.

The API writes a `MarketingPromotion` notification-outbox job. No migration,
customer account, order, invoice or inventory change is required. The local
Admin-authenticated `/api/marketing-promotions/pending` endpoint leases one job;
`/{id}/result` records completion or a safe retry. Other notification channels
are never claimed. The request's status button remains usable after restarts.

`admin-promotions.py` runs as the existing `zibashe` operational user. It loads
runtime configuration through the existing Docker helper, retains secrets only
in memory, and uses normal HTTPS certificate validation. SQL catalogue access
remains read-only; queue updates go through the authenticated local API.
Install to `~/.local/bin/zibashe-admin-promotions.py` and add only this cron entry:

```cron
* * * * * /usr/bin/python3 /home/zibashe/.local/bin/zibashe-admin-promotions.py --run-seconds 55 >/dev/null 2>&1 # zibashe-admin-promotions
```

The worker polls every three seconds during each minute and processes a request
to completion. An exclusive worker lock prevents overlapping cron processes.
It uses existing checked image/review caches, generating and verifying missing
content with the established helpers. Group requests prefer three checked images;
if one cannot be prepared or a cached file is unavailable, two healthy verified
images are sent with the normal text. Failed generation of the wearer image
does not block two verified product photos. Fewer than two verified images still
requires preparation. Unknown Telegram album delivery is never retried as a
smaller album, because that could duplicate an accepted post. The album is
followed by a separate readable text reply with an order button. Existing permanent
group exclusions remain enforced. Explicit administrator requests use manual
history slots and leave the regular two-hour slot schedule unchanged.

Channel requests use the existing promotion engine and a persisted 24-hour
deadline. Request IDs survive active/retired channel state so a restart does not
post again or extend the deadline. Completion still removes a promotion early.
The existing channel automation resumes when a manual promotion expires.

State and rotating logs live in `~/.local/state/zibashe-admin-promotions/`.
Each album and text send has a separately fsynced intent and receipt. Safe
preparation failures retry up to five times. Ambiguous HTTP outcomes require
review; they are never blindly resent. A known album receipt allows a restart
to continue with only the missing text. Keep these journals while a request may
be retried; deleting them defeats duplicate prevention.

Validation uses synthetic Telegram/catalogue fixtures and the guarded isolated
`ZibasheERPCloudDev` SQL database; never create test advertising jobs on production.

```sh
python3 deploy/tests/admin-promotions.test.py
python3 deploy/tests/channel-remaining-promotion.test.py
```

The «حذف تبلیغ کانال» menu action asks for confirmation and queues a distinct
channel-removal control job. It requires the same administrator authorization.
The channel engine durably pauses before deleting only its recorded promotion;
original sales-list posts and group advertising remain unchanged. Subsequent
scheduled ticks keep the channel paused until a newer explicit channel selection.
Older queued publication/removal jobs cannot undo a more recent administrator
choice. Completed removal receipts prevent deletion from being replayed after a
crash. Telegram refusal to delete an aged message is reported as an archive,
never as a successful deletion.

Channel code entry now stages a per-admin choice before queueing: keep prior ads
(`ChannelMode=append`) or replace all prior ads (`ChannelMode=replace`, also the
backward-compatible default for older jobs). Choice tokens bind a button to the
entered code and expire with the draft; stale buttons cannot publish a later
code. Stock is checked again when the administrator confirms the choice.

The channel state retains its legacy `active` slot and stores additional,
independently journaled slots under `additional`. Every scheduler tick checks
all slots. Each ad refreshes its own remainder and retires when its own list
completes or its existing deadline expires. Automatic selections exclude lists
already advertised in other slots. Retained-ad receipts survive restarts and
remain discoverable after completion. Removing channel ads pauses and retires
all tracked slots; selecting replacement retires prior ads before sending the
new one. Group publishing and regular schedules are unchanged.

Only this same owner may use «پروموشن ساعتی». Code, duration in hours and
percentage or fixed Toman discount per ml are entered before a photo/caption
preview and token-bound confirmation. `flash-start` and `flash-end` outbox jobs
use the hourly engine under its own lock. Start rechecks the approved base price;
retrying cannot extend the campaign. Early-end confirmation stops pricing in
the webhook transaction immediately, then durably queues closing announcements.
The existing minute cron updates countdowns and sends a thank-you once to each
destination on expiry. Legacy campaigns without automatic-lifecycle profiles
are not reannounced. See `flash-sale-promotion.md`.

Ready inventory uses distinct `inventory-publish` and `inventory-report` jobs.
The authenticated `/api/inventory-offers/{id}/publication` endpoint returns only
public text and the configured destination, never a storage position. The
dispatcher verifies the exact package and destination, journals photo/text
receipts separately and binds them through `/receipt`. Inventory invoice reports
are enqueued within the invoice transaction; accepted inventory-photo receipts
are reused after a binding failure. `Telegram__ReadyInventoryChannelId` defaults
to `@zibasheperfume_mojodi`; the old returned-inventory destination is preserved.

## Administrator-provided advertising copy

After a valid list code, both group and channel flows ask for Ziba copy or
administrator copy. Ziba keeps the existing generation path. Administrator copy
is accepted as plain text, previewed and stored verbatim in `CustomText` on the
durable job; HTML-like text is escaped at publication. Group input permits 3500
UTF-16 units; channel photo captions permit a 700-unit body plus the generated
stock/code footer. Token-bound group confirmation and the channel keep/replace
choice approve publication. Editing input invalidates old buttons. Duplicate
protection distinguishes different approved copy for the same list.

Custom group copy keeps the existing verified three/two-photo workflow and
trusted purchase button, while skipping automatic copy research. The channel
engine stores `custom_text` with each independent ad; remainder edits, retries,
restarts and renewals retain it. Choosing Ziba again explicitly clears the old
custom copy. Channel completion removal and existing schedules remain unchanged.
