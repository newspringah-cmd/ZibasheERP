import json
import pathlib
import re

ROOT = pathlib.Path(__file__).resolve().parents[1]
UUID = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", re.I)
GROUP_CHAT = re.compile(r"^-[1-9][0-9]*$")


def load(relative_path):
    with (ROOT / relative_path).open(encoding="utf-8") as source:
        return json.load(source)


event = load("samples/invoice-issued.json")
artifact = load("samples/artifact-callback.json")
failure = load("samples/delivery-failure.json")

assert event["eventType"] == "InvoiceIssued"
assert UUID.fullmatch(event["eventId"])
assert UUID.fullmatch(event["orderId"])
assert event["data"]["OrderId"] == event["orderId"]
assert event["data"]["Delivery"]["Channel"] == "TelegramGroup"
assert GROUP_CHAT.fullmatch(event["data"]["Delivery"]["ChatId"])

assert artifact["sourceEventId"] == event["eventId"]
assert artifact["orderId"] == event["orderId"]
assert artifact["type"] == "InvoicePdf"
assert artifact.get("fileUrl") or artifact.get("externalFileId")
assert artifact["contentType"] == "application/pdf"

assert failure["sourceEventId"] == event["eventId"]
assert failure["chatId"] == event["data"]["Delivery"]["ChatId"]
assert 1 <= len(failure["error"]) <= 1000

print("n8n contract samples test: PASS")
