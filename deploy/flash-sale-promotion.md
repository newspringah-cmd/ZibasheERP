# Configurable owner-approved hourly promotions

The Admin-only `/api/flash-sales` endpoint creates an idempotent campaign using
its supplied UUID and the verified configured owner numeric ActorUserId.
It supports 1–10080 minutes, integer 1–99 percent or a fixed positive Toman
discount per ml. A fixed amount requires DiscountPercent=0; discounted price
must remain positive. ExpectedOriginalPricePerMl binds activation to the preview.
The SalesList `PricePerMl` is never changed. Subsequent bottle cycles of the same
perfume and stable public list code opened within the offer inherit its original
campaign and deadline. Another perfume, public list, inventory offer, or cycle
opened after the deadline does not inherit it. The status API follows the latest
cycle owning the original channel post so both countdowns continue through rollover.

Customer selection quotes the active perfume price. Final confirmation checks
the deadline again under the existing list reservation lock and records the
campaign ID and accepted perfume price on SalesListRequests. Merely clicking a
volume does not secure the discount. Expired discounted selections must start
again so the customer sees the ordinary price before confirming.

Invoice preview and issuance retain the confirmed promotional snapshot after
expiry. Ordinary requests continue to follow final administrator list pricing.
Bottle charges retain their existing accounting rules. Bulk list caption edits
retain the promotional snapshot and original confirmation timestamp. Expired
promotional items cannot be enlarged through the standard item-edit operation.
Never delete historical FlashSalePromotions: their references audit eligibility.

`flash-sale-promotion.py --publish CAMPAIGN_UUID` sends the approved copy and
actual perfume photo to group -1001283018740, with a link and purchase button to
the list's channel post. An intent and fsynced receipt prevent blind duplicate
publication. An unknown send requires group inspection; automatic ticks never
publish a missing ad. API receipt binding can safely retry after a restart.

The dedicated minute cron calls the script without arguments to edit the same
caption. At the exclusive deadline it marks the offer ended and replaces the
purchase call to action. Pricing expires by server time even if Telegram edits
are temporarily unavailable. Failed edits retry on the next minute, and an
already-applied edit is accepted after a crash. Other advertising schedules,
channel advertisements, invoices, repository permissions and credentials are
untouched.

State: `~/.local/state/zibashe-flash-sales/CAMPAIGN_UUID/`. Keep the send receipt
and campaign row for auditing. No token, API key or customer details are saved
in this directory. Tests use a development SQL database and fake transports.

An explicitly requested channel copy uses `--publish-channel CAMPAIGN_UUID`.
Its receipt and countdown are stored under `CAMPAIGN_UUID/channel/`. It reads
the existing campaign and never activates or extends an offer. The group receipt
and ordinary channel advertisements remain unchanged. The existing minute cron
updates both destinations independently until the original deadline.

## Owner menu and lifecycle

`/admin` → «پروموشن ساعتی» is restricted to the configured primary owner
@zahraa_frj. A fresh photo/caption preview and explicit confirmation precede any
activation or group/channel publication. `admin-promotions.py` supplies approved
parameters, retains its durable queue receipt and calls this worker under run.lock.
The group and channel share one deadline and each have independent send receipts.
The owner can end early after confirmation: the API locks the related lists and
sets the exclusive deadline immediately; already-confirmed item prices survive.
`automatic-lifecycle.json` marks newly approved campaigns for the existing minute
cron. It edits both countdowns and, at expiry, sends a separate thank-you message
to each destination. End receipts are durable and ambiguous sends stop for review.
Failure to edit a deleted old ad does not block the fresh thank-you. Legacy ended
campaigns without the profile are never reannounced. No public test campaign is
needed for deployment. The historical 4559 campaign remains ended.
