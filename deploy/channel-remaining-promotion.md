This job keeps an automatically selected promotion in `@zibasheperfume` for an
open sales list with a remainder strictly between zero and ten ml. Administrators
may retain additional explicitly selected promotions; each is monitored and
retired independently when its list completes or its own deadline expires. It reads the production catalogue;
it does not change reservations, orders, payments or stock.

Install the reviewed script as
`/home/zibashe/.local/bin/zibashe-channel-remaining-promotion.py` and run it every
minute from the existing user's crontab:

```cron
* * * * * /usr/bin/python3 /home/zibashe/.local/bin/zibashe-channel-remaining-promotion.py >/dev/null 2>&1 # zibashe-channel-remaining-promotion
```

The schedule has no expiry. The caption and original-list button use the live
remainder. When the list completes, closes, disappears, or ceases to qualify, the
job deletes its recorded promotion and selects another qualifying perfume. If
none qualifies, it waits and resumes automatically. It favors a different
perfume and then the least recently advertised one. The original sales posts
are never edited or deleted. Closed cycles may clear their channel metadata;
cleanup uses the previously recorded promotion receipt instead.

An explicit `--select-code` request may select a list with ten ml or more. That
exception belongs only to the chosen active promotion, including its 36-hour
renewals. Its live remainder still updates normally. On completion, the next
automatic selection again requires strictly less than ten ml. The requested
replacement is journaled before deletion, so a restart preserves the selection.
Use `--select-code 7355 --hold-hours 24` to end a manual promotion after 24 hours
and resume automatic selection. The persisted deadline survives restarts; live
remaining-volume updates and early removal on completion continue normally.
The one-minute schedule processes expiry on its next run.

The job replaces a still-active promotion after 36 hours so its message stays
within Telegram's 48-hour deletion window. After an unusually long server outage,
if Telegram refuses deletion, the job removes the old purchase button and stock
claim, marks the promotion ended, and proceeds to the next eligible item.

Runtime state and rotating logs live in
`~/.local/state/zibashe-channel-promotion/`, outside the checkout. Do not remove
the state while an advertisement is live. An exclusive process lock, atomic
fsynced state writes, and a send intent persisted before HTTP prevent overlapping
jobs and blind duplicate sends. Ambiguous sends are reconciled against the exact
caption and timestamp in the public channel preview; if the result cannot be
established, the job keeps checking without sending another copy. Known Telegram
rate limits and failed idempotent edits/deletions are retried on later runs.

The existing API container supplies the configured bot token in memory. No token
or password is written into the script, crontab, state or logs. HTTP uses normal
certificate verification. The SQL command uses the existing container's local
SQL configuration, performs only SELECT queries, and never copies database
credentials to the workstation. Installation does not restart the API or SQL
Server and does not change environment or repository permissions.

```sh
# Inspect without posting.
python3 deploy/channel-remaining-promotion.py --preview
python3 deploy/channel-remaining-promotion.py --status

# Replace the active promotion with a user-selected open list.
python3 deploy/channel-remaining-promotion.py --select-code 3782

# Exercise inventory transitions, restart/timeout recovery and duplicate prevention.
python3 deploy/tests/channel-remaining-promotion.test.py
```

To stop the automation, remove only the crontab entry bearing the
`zibashe-channel-remaining-promotion` marker. Retain its durable state so the
recorded live message remains identifiable. The two-hour group publisher is a
separate job and is unaffected.

Administrator removal pauses the channel engine durably before retiring its
recorded ad. Cron remains installed but does not select another ad while paused.
A newer explicit selection resumes it. Persisted control timestamps and removal
receipts prevent delayed jobs from undoing a later administrator choice.

Manual selections may contain `custom_text` (plain text, at most 700 UTF-16
units). The engine escapes it and adds the live remainder and list code. Each
active slot persists its own body so restarts, stock edits and renewal never
replace it with automatic copy. Selecting the same ad with no custom text
explicitly restores automatic copy. Completion retirement is unchanged.
