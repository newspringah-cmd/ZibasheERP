# Automatic AED rate from Alanchand

Source: https://alanchand.com/currencies-price

Enabled by default when Telegram is enabled. Disable explicitly with
`Telegram__AutomaticDirhamRateEnabled=false`. No database migration is required.

The live source was retrieved over HTTPS with status 200 on 2026-11-13 after the
reviewed network policy update was applied. Its AED row displayed **74,150 toman
selling** and **73,410 toman buying**; the unit appears in the nearest enclosing
section's `v4-ltbl__sub` header ("اسکناس · تومان"). The adapter reads that section
unit together with the selling column and visible AED price, rather than page
metadata, the buying column or the "one dollar" conversion. A regression fixture
preserves this observed layout. Current prices may change; this is verification
evidence, not a permanently configured rate.

The adapter accepts explicitly labeled HTML tables. If the provider switches to
client-rendered data or changes its labels, a failed parse retains the prior rate.
The registered HTTP client intentionally does not follow redirects automatically.

The worker fetches on startup if due, then every two hours after a successful
record. Failures retry on the five-minute scan. Cadence is durable across
restarts and serialized under the same SQL application lock as manual entry.
Only the labeled AED selling price is used (a single labeled price column is
also accepted); explicitly labeled rial values are divided by ten. Unknown
units, multiple disagreeing AED quotes, invalid amounts, HTML challenges,
non-HTML responses and oversized documents leave the ledger unchanged.

Each successful observation appends a `DirhamExchangeRates` entry with
`RecordedByUserId=0` and a processed `AlanchandDirhamRate` receipt in the same
transaction. Receipt and rate share an ID. The receipt stores the source URL,
unit, column, raw price, fetch/effective times and document hash. There are no
customer notifications. The owner-only rate menu displays the source and last
successful automatic update when enabled; manual rate entry remains available.

Rates take effect when recorded, never retroactively. Purchase-cost calculations
continue selecting the rate effective at each list's completion time. Automatic
updates do not alter perfume sale prices or customer invoices. A manual override
remains effective until a later automatic or manual entry.

Validation uses the isolated `ZibasheERPCloudDev` database and
`ZibasheERP.API.Tests --automatic-dirham`. Roll back activation by setting the
flag to false; preserve historical rows and receipts.
