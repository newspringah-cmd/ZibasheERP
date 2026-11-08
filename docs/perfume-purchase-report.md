# Owner-only perfume purchasing report

The private administrator menu places «گزارش جهت خرید عطر» beside the inventory financial report. Only the configured stable numeric primary owner (currently @zahraa_frj) can open or execute it; worker delivery independently validates the same owner and private destination.

Thresholds are cumulative and strict: positive remaining volume / total volume < 10%, 20%, 30% or 50%. Only open ordinary lists are eligible, excluding ready inventory, completed/cancelled lists and invalid zero totals. Results sort by remaining percentage and code, without a display cap.

Each result contains Persian and English names, list code, total and remaining ml, percentage, stored perfume photo and exact Telegram source-post link. Missing source photos fall back only to the matching perfume variant, then to a clearly labelled text card; unavailable post links are explicitly labelled. No list is silently excluded for missing media.

A dedicated durable outbox stream delivers one card at a time, rechecking current eligibility. It honours Telegram 429 retry_after, resumes after restarts and never blindly repeats unknown send outcomes; a final summary identifies unsuccessful/uncertain counts. Duplicate clicks while a report is active do not create overlapping reports. No list, request, stock quantity, invoice or financial value is changed. The isolated-SQL tests use fake Telegram only.
