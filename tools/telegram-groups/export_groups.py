from __future__ import annotations

import argparse
import asyncio
import csv
import os
from datetime import datetime, timezone
from pathlib import Path

from telethon import TelegramClient, utils
from telethon.tl.types import Channel, Chat


FIELDS = (
    "chat_id",
    "title",
    "username",
    "group_type",
    "is_archived",
    "is_creator",
    "is_admin",
    "participants_count",
    "exported_at_utc",
    "status",
)


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
    comparable = ("title", "username", "group_type")
    return (
        "changed"
        if any(str(row[key] or "") != previous.get(key, "") for key in comparable)
        else "existing"
    )


async def export() -> None:
    args = parse_args()
    api_id_text = os.environ.get("TELEGRAM_API_ID", "").strip()
    api_hash = os.environ.get("TELEGRAM_API_HASH", "").strip()
    phone = os.environ.get("TELEGRAM_PHONE", "").strip() or None
    if not api_id_text or not api_hash:
        raise SystemExit("Set TELEGRAM_API_ID and TELEGRAM_API_HASH first.")

    previous = read_previous(args.previous)
    exported_at = datetime.now(timezone.utc).isoformat()
    rows: list[dict[str, object]] = []

    async with TelegramClient(args.session, int(api_id_text), api_hash) as client:
        if not await client.is_user_authorized():
            await client.start(phone=phone)

        async for dialog in client.iter_dialogs():
            entity = dialog.entity
            is_basic_group = isinstance(entity, Chat)
            is_supergroup = isinstance(entity, Channel) and bool(entity.megagroup)
            if not (is_basic_group or is_supergroup):
                continue

            chat_id = str(utils.get_peer_id(entity))
            row: dict[str, object] = {
                "chat_id": chat_id,
                "title": dialog.name or "",
                "username": getattr(entity, "username", None) or "",
                "group_type": "supergroup" if is_supergroup else "group",
                "is_archived": dialog.folder_id == 1,
                "is_creator": bool(getattr(entity, "creator", False)),
                "is_admin": bool(getattr(entity, "admin_rights", None)),
                "participants_count": getattr(entity, "participants_count", None) or "",
                "exported_at_utc": exported_at,
            }
            row["status"] = classify_status(row, previous.get(chat_id))
            rows.append(row)

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
