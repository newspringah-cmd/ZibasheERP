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
workflow = load("workflows/production-events.json")

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

nodes = {node["name"]: node for node in workflow["nodes"]}
connections = workflow["connections"]
assert nodes["Zibashe Events"]["parameters"]["responseMode"] == "responseNode"
assert nodes["Record PDF Artifact"]["onError"] == "continueRegularOutput"
assert "Acknowledge Invoice Delivery" in nodes
telegram_delivery = nodes["Send PDF To Customer Group"]
assert telegram_delivery["type"] == "n8n-nodes-base.httpRequest"
assert telegram_delivery["parameters"]["url"].endswith("/api/integrations/n8n/telegram-invoices")
assert telegram_delivery["parameters"]["contentType"] == "multipart-form-data"
assert any(
    parameter.get("parameterType") == "formBinaryData" and parameter.get("name") == "document"
    for parameter in telegram_delivery["parameters"]["bodyParameters"]["parameters"]
)
assert connections["Send PDF To Customer Group"]["main"][0][0]["node"] == "Build Artifact Callback"
assert connections["Record PDF Artifact"]["main"][0][0]["node"] == "Acknowledge Invoice Delivery"

print("n8n contract samples test: PASS")
