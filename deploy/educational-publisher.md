The educational publisher posts one short fragrance-selection guide plus a reply
album containing one actual catalogue photo per recommended fragrance. It uses
18 rotating topics and selects three distinct available fragrances, prioritizing
remainders below 20 ml. Topic-specific recommendations match catalogue notes.
The group exclusion policy (including list 7888) and the shared two-per-fragrance
daily cap apply. This is a separate format from the direct promotion publisher.

Deployment installs a runtime copy at
`~/.local/bin/zibashe-educational-publisher.py`. A five-minute cron tick checks a
durable two-hour window offset one hour from the existing direct advertisements.
Only one guide is sent per window; the frequent tick permits recovery of a saved
text receipt before the photo phase and rechecking when fewer than three suitable
fragrances are available. There is no schedule expiry.

The `education_campaigns` table in the existing marketing SQLite database records
the text and photo phases. Intents are committed before Telegram calls. An
ambiguous send is never blindly repeated; the next window can still proceed.
Each recommendation is recorded in shared marketing history for frequency caps.
The group publisher lock serializes both formats. Logs rotate under
`~/.local/state/zibashe-education/`.

No ERP writes, customer data, or production service restarts are needed. Existing
API credentials remain in memory and normal TLS verification is used. Inspect
with `--preview` (no messages) or `--status`. To disable, remove only the cron line
marked `zibashe-educational-publisher`; retain history so sent messages remain
identifiable. Tests: `python3 -B deploy/tests/educational-publisher.test.py`.
