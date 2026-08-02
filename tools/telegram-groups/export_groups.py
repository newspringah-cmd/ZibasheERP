from __future__ import annotations

import argparse
import asyncio
import csv
import getpass
import os
import re
from datetime import datetime, timezone
from pathlib import Path

from telethon import TelegramClient, utils
from telethon.tl.types import Channel, Chat


FIELDS = (
    "chat_id",
    "title",
    "username",
    "customer_username",
    "group_type",
    "is_archived",
    "is_creator",
    "is_admin",
    "participants_count",
    "exported_at_utc",
    "status",
)

CUSTOMER_USERNAME_PATTERN = re.compile(r"@([A-Za-z0-9_]{5,32})")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Export every Telegram group visible to a user account."
    )
    parser.add_argument("--output", default="output/telegram-groups-current.csv")
    parser.add_argument(
        "--previous",
        help="Previous CSV export. When supplied, status is new, existing, or changed.",
    )
    parser.add_argument(
        "--removed-output",
        default="output/telegram-groups-missing.csv",
        help="Rows present in the previous file but absent from the current account.",
    )
    parser.add_argument("--session", default="zibashe-admin")
    return parser.parse_args()


def read_previous(path: str | None) -> dict[str, dict[str, str]]:
    if not path:
        return {}
    with Path(path).open("r", encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        if "chat_id" not in (reader.fieldnames or []):
            raise ValueError("Previous CSV must contain a chat_id column.")
        return {
            row["chat_id"].strip(): row
            for row in reader
            if row.get("chat_id", "").strip()
        }


def write_csv(path: Path, rows: list[dict[str, object]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=FIELDS, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(rows)


def classify_status(row: dict[str, object], previous: dict[str, str] | None) -> str:
    if previous is None:
        return "new"

    current_title = str(row["title"] or "").lstrip("'").strip()
    previous_title = (previous.get("title") or previous.get("group_name") or "").lstrip("'").strip()
    if current_title != previous_title:
        return "changed"

    optional_fields = ("username", "group_type", "customer_username")
    for key in optional_fields:
        if key in previous and previous[key].strip() != str(row[key] or "").strip():
            return "changed"
    return "existing"


def extract_customer_username(title: str) -> str:
    match = CUSTOMER_USERNAME_PATTERN.search(title)
    return f"@{match.group(1).lower()}" if match else ""


async def export() -> None:
    args = parse_args()
    api_id_text = os.environ.get("TELEGRAM_API_ID", "").strip()
    api_hash = os.environ.get("TELEGRAM_API_HASH", "").strip()
    phone = os.environ.get("TELEGRAM_PHONE", "").strip()
    if not api_id_text:
        api_id_text = input("Telegram API ID: ").strip()
    if not api_hash:
        api_hash = getpass.getpass("Telegram API Hash (hidden): ").strip()
    if not phone:
        phone = input("Telegram phone (example +98912...): ").strip()
    if not api_id_text.isdigit() or not api_hash or not phone:
        raise SystemExit("API ID, API Hash, and phone are required.")

    previous = read_previous(args.previous)
    exported_at = datetime.now(timezone.utc).isoformat()
    rows: list[dict[str, object]] = []

    print("Connecting to Telegram...", flush=True)
    client = TelegramClient(args.session, int(api_id_text), api_hash)
    await client.connect()
    try:
        if not await client.is_user_authorized():
            print("Login is required. Enter the Telegram code when prompted.", flush=True)
            await client.start(phone=phone)
        else:
            print("Existing Telegram session loaded.", flush=True)

        async for dialog in client.iter_dialogs():
            entity = dialog.entity
            is_basic_group = isinstance(entity, Chat)
            is_supergroup = isinstance(entity, Channel) and bool(entity.megagroup)
            if not (is_basic_group or is_supergroup):
                continue

            chat_id = str(utils.get_peer_id(entity))
            title = dialog.name or ""
            row: dict[str, object] = {
                "chat_id": chat_id,
                "title": title,
                "username": getattr(entity, "username", None) or "",
                "customer_username": extract_customer_username(title),
                "group_type": "supergroup" if is_supergroup else "group",
                "is_archived": dialog.folder_id == 1,
                "is_creator": bool(getattr(entity, "creator", False)),
                "is_admin": bool(getattr(entity, "admin_rights", None)),
                "participants_count": getattr(entity, "participants_count", None) or "",
                "exported_at_utc": exported_at,
            }
            row["status"] = classify_status(row, previous.get(chat_id))
            rows.append(row)
            if len(rows) % 250 == 0:
                print(f"Scanned {len(rows)} groups...", flush=True)
    finally:
        await client.disconnect()

    rows.sort(key=lambda row: (str(row["title"]).casefold(), str(row["chat_id"])))
    output_path = Path(args.output)
    write_csv(output_path, rows)

    current_ids = {str(row["chat_id"]) for row in rows}
    missing = []
    for chat_id, old_row in previous.items():
        if chat_id not in current_ids:
            missing.append({**old_row, "status": "missing"})
    if args.previous:
        write_csv(Path(args.removed_output), missing)

    counts = {status: 0 for status in ("new", "existing", "changed")}
    for row in rows:
        counts[str(row["status"])] += 1
    print(f"Exported {len(rows)} groups to {output_path}")
    print(
        f"new={counts['new']} existing={counts['existing']} "
        f"changed={counts['changed']} missing={len(missing)}"
    )


if __name__ == "__main__":
    asyncio.run(export())
