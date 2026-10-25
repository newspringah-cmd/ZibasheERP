The perfume promotion publisher runs on the VPS as the existing `zibashe` user.
It requires Python 3, Docker access, the running API/SQL containers, and the existing
AddressLabel OpenAI configuration. It reads only public product catalogue fields.

Run `python3 deploy/marketing-publisher.py --dry-run` to verify research, content
validation and fresh list eligibility without sending a message. The optional
`--test-code CODE` is an explicit manual sample, bypassing the remaining-volume
threshold and same-list cooldown; the two-per-perfume 24-hour cap still applies.
Never use it in scheduled execution.

The server crontab runs the script every two hours. State, research sources, slot
claims and message receipts are stored in `~/.local/state/zibashe-marketing/` with
private permissions. Failed/unknown delivery is never automatically repeated in
the same slot. The same perfume is limited to two sent or possibly sent ads in
any rolling 24-hour period, and an individual sales list remains on a seven-day cooldown.
The publisher prioritizes lists with less than 10 ml remaining; when none are
eligible, it falls back to lists with less than 20 ml remaining.
No eligible new list means no message. Research failure means no advertisement;
the reason is stored in the state database and publisher log.

Destination is the explicitly authorized Zibashe perfume discussion group.
Incoming bot replies remain suppressed using `Telegram__SilentGroupChatIds`.
Stopping the cron entry disables publishing without affecting the ERP API.

After the primary promotion, an album of exactly three images from the matching
Fragrantica perfume page or its Fragram gallery is sent when verified. Verified
image sets are saved on the VPS using the perfume brand, exact
variant name and product page identity, and reused if the perfume appears in a
different sales list. Review research text is shared across those lists and
refreshed every 30 days. If fewer than three safe, distinct images including a
wearer photo are found on Fragrantica, the album is skipped; the primary ad still
publishes. Images must come from the exact Fragrantica perfume page or its
Fragram gallery.
Vision verification rejects wrong variants, retailer names and overlaid watermarks.
Bottle branding is allowed. Images are not edited or watermarks removed. Album
claims/receipts are separate from the primary post to prevent ambiguous retries.
The album consists of two Fragrantica product/packaging photos followed by one
Fragrantica lifestyle image with a clearly visible adult human wearer, prioritizing
women where the fragrance positioning allows. If no suitable wearer photo is
available, the album is skipped. Album photos have no captions. Older image
caches are rebuilt to enforce the Fragrantica-only source requirement.
