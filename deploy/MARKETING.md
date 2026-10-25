The perfume promotion publisher runs on the VPS as the existing `zibashe` user.
It requires Python 3, Docker access, the running API/SQL containers, and the existing
AddressLabel OpenAI configuration. It reads only public product catalogue fields.

Run `python3 deploy/marketing-publisher.py --dry-run` to verify research, content
validation and fresh list eligibility without sending a message. The optional
`--test-code CODE` is an explicit manual sample, bypassing only the remaining-volume
threshold and cooldown; never use it in scheduled execution.

The server crontab runs the script every two hours. State, research sources, slot
claims and message receipts are stored in `~/.local/state/zibashe-marketing/` with
private permissions. Failed/unknown delivery is never automatically repeated in
the same slot. A seven-day cycle cooldown prevents repetitive advertising.
No eligible new list means no message. Research failure means no advertisement;
the reason is stored in the state database and publisher log.

Destination is the explicitly authorized Zibashe perfume discussion group.
Incoming bot replies remain suppressed using `Telegram__SilentGroupChatIds`.
Stopping the cron entry disables publishing without affecting the ERP API.

After the primary promotion, an album of exactly three official product images is
sent when verified. The initial first-party registry covers Diptyque, Creed and
Roja. Verified image sets are saved on the VPS using the perfume brand, exact
variant name and product page identity, and reused if the perfume appears in a
different sales list. Unsupported brands or fewer than three safe distinct images skip the album;
the primary ad still publishes. Sources must come from the brand product page.
Vision verification rejects wrong variants, retailer names and overlaid watermarks.
Bottle branding is allowed. Images are not edited or watermarks removed. Album
claims/receipts are separate from the primary post to prevent ambiguous retries.
The album consists of two product/packaging photos followed by one official
lifestyle/campaign image conveying the wearer's style. If the official lifestyle
image is unavailable, an independent AI concept image is generated without a
fabricated bottle or brand logo. It carries an explicit AI-generated caption.
The first two photos must still be verified official product images, or the album
is skipped. `Marketing__ImageModel` defaults to `gpt-image-2` for this conditional
generation. Cached older bottle-only albums are revalidated.
