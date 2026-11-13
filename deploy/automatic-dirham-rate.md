# Automatic AED rate from Alanchand

Source: https://alanchand.com/currencies-price

Status: implemented but **disabled by default**. The live Alanchand document has
not yet been verified: the development environment rejects the domain with a
network-policy HTTP 403. A draft adds `alanchand.com` while preserving the other
pending and published domain rules. Reviewing and publishing that draft is
required before live source verification. Do not enable the worker using only
the synthetic parser fixtures as evidence of source compatibility.

After access is available, save a bounded copy of the actual source, verify the
AED row, buy/sell column, denomination and displayed numeric value, and run the
parser against it. The current adapter accepts explicitly labeled HTML tables;
if the provider instead returns client-rendered data or a different layout,
adapt the parser to the observed source and retain a redacted source fixture.
Do not infer prices from page-wide numbers. Verify redirects before enabling;
the registered HTTP client intentionally does not follow them automatically.

Once the live result is independently verified, enable
`Telegram__AutomaticDirhamRateEnabled=true` in the existing production release
workflow, preserving all current environment values and services. No database
migration is required. Deploying source alone does not enable the worker.

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
