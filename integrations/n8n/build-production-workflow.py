import base64
import json
from pathlib import Path

root = Path(__file__).resolve().parent
validate = (root / "code" / "validate-event-metadata.js").read_text(encoding="utf-8")
invoice = (root / "code" / "build-invoice-html.js").read_text(encoding="utf-8")
background = base64.b64encode((root / "assets" / "invoice-background.jpeg").read_bytes()).decode("ascii")
invoice = invoice.replace(
    "__INVOICE_BACKGROUND_DATA_URI__",
    f"data:image/jpeg;base64,{background}")

binary_code = r'''const item = $input.first();
const html = String(item.json.invoiceHtml ?? '');
if (!html) throw new Error('Invoice HTML is empty.');
return [{json: item.json, binary: {data: {data: Buffer.from(html, 'utf8').toString('base64'), mimeType: 'text/html', fileName: 'index.html'}}}];'''

artifact_code = r'''const event = $('Build Invoice HTML').first().json;
const telegram = $input.first().json;
const fileId = telegram.document?.file_id ?? telegram.result?.document?.file_id;
if (!fileId) throw new Error('Telegram did not return the PDF file_id.');
return [{json: {sourceEventId: event.eventId, orderId: event.orderId, type: 'InvoicePdf', fileUrl: null, externalFileId: fileId, contentType: 'application/pdf'}}];'''

nodes = [
  {"id":"webhook","name":"Zibashe Events","type":"n8n-nodes-base.webhook","typeVersion":2,"position":[0,0],"parameters":{"httpMethod":"POST","path":"zibashe-events","authentication":"headerAuth","responseMode":"responseNode","options":{}}},
  {"id":"validate","name":"Validate Event","type":"n8n-nodes-base.code","typeVersion":2,"position":[240,0],"parameters":{"mode":"runOnceForAllItems","jsCode":validate}},
  {"id":"invoice-if","name":"Is Invoice Issued","type":"n8n-nodes-base.if","typeVersion":2.2,"position":[480,0],"parameters":{"conditions":{"options":{"caseSensitive":True,"leftValue":"","typeValidation":"strict","version":2},"conditions":[{"id":"invoice-condition","leftValue":"={{ $json.eventType }}","rightValue":"InvoiceIssued","operator":{"type":"string","operation":"equals"}}],"combinator":"and"},"options":{}}},
  {"id":"build-html","name":"Build Invoice HTML","type":"n8n-nodes-base.code","typeVersion":2,"position":[720,-80],"parameters":{"mode":"runOnceForAllItems","jsCode":invoice}},
  {"id":"html-binary","name":"HTML To Binary","type":"n8n-nodes-base.code","typeVersion":2,"position":[960,-80],"parameters":{"mode":"runOnceForAllItems","jsCode":binary_code}},
  {"id":"gotenberg","name":"Create PDF","type":"n8n-nodes-base.httpRequest","typeVersion":4.2,"position":[1200,-80],"parameters":{"method":"POST","url":"http://gotenberg:3000/forms/chromium/convert/html","sendBody":True,"contentType":"multipart-form-data","bodyParameters":{"parameters":[{"parameterType":"formBinaryData","name":"files","inputDataFieldName":"data"}]},"options":{"response":{"response":{"responseFormat":"file","outputPropertyName":"data"}}}}},
  {"id":"telegram","name":"Send PDF To Customer Group","type":"n8n-nodes-base.httpRequest","typeVersion":4.2,"position":[1440,-80],"parameters":{"method":"POST","url":"https://erp.zibashe.ir/api/integrations/n8n/telegram-invoices","authentication":"genericCredentialType","genericAuthType":"httpHeaderAuth","sendBody":True,"contentType":"multipart-form-data","bodyParameters":{"parameters":[{"parameterType":"formData","name":"sourceEventId","value":"={{ $('Build Invoice HTML').first().json.eventId }}"},{"parameterType":"formData","name":"chatId","value":"={{ $('Build Invoice HTML').first().json.data.Delivery.ChatId }}"},{"parameterType":"formData","name":"caption","value":"={{ $('Build Invoice HTML').first().json.telegramCaption }}"},{"parameterType":"formBinaryData","name":"document","inputDataFieldName":"data"}]},"options":{}}},
  {"id":"respond","name":"Acknowledge Invoice Delivery","type":"n8n-nodes-base.respondToWebhook","typeVersion":1.4,"position":[1680,-180],"parameters":{"respondWith":"json","responseBody":"={{ { \"ok\": true, \"delivered\": true } }}","options":{"responseCode":200}}},
  {"id":"artifact","name":"Build Artifact Callback","type":"n8n-nodes-base.code","typeVersion":2,"position":[1680,-80],"parameters":{"mode":"runOnceForAllItems","jsCode":artifact_code}},
  {"id":"record","name":"Record PDF Artifact","type":"n8n-nodes-base.httpRequest","typeVersion":4.2,"position":[1920,-80],"onError":"continueRegularOutput","parameters":{"method":"POST","url":"https://erp.zibashe.ir/api/integrations/n8n/order-artifacts","authentication":"genericCredentialType","genericAuthType":"httpHeaderAuth","sendBody":True,"contentType":"raw","rawContentType":"application/json","body":"={{ JSON.stringify($json) }}","options":{}}}
]

workflow = {"id":"zibashe-production-events","name":"Zibashe Production Events","nodes":nodes,"pinData":{},"connections":{
  "Zibashe Events":{"main":[[{"node":"Validate Event","type":"main","index":0}]]},
  "Validate Event":{"main":[[{"node":"Is Invoice Issued","type":"main","index":0}]]},
  "Is Invoice Issued":{"main":[[{"node":"Build Invoice HTML","type":"main","index":0}],[]]},
  "Build Invoice HTML":{"main":[[{"node":"HTML To Binary","type":"main","index":0}]]},
  "HTML To Binary":{"main":[[{"node":"Create PDF","type":"main","index":0}]]},
  "Create PDF":{"main":[[{"node":"Send PDF To Customer Group","type":"main","index":0}]]},
  "Send PDF To Customer Group":{"main":[[{"node":"Build Artifact Callback","type":"main","index":0}]]},
  "Build Artifact Callback":{"main":[[{"node":"Record PDF Artifact","type":"main","index":0}]]},
  "Record PDF Artifact":{"main":[[{"node":"Acknowledge Invoice Delivery","type":"main","index":0}]]}
},"active":False,"settings":{"executionOrder":"v1"},"versionId":"00000000-0000-4000-8000-000000000001","meta":{"templateCredsSetupCompleted":False},"tags":[]}

output = root / "workflows" / "production-events.json"
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(json.dumps(workflow, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
