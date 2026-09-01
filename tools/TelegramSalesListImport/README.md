# Telegram sales-list import

This tool reads a Telegram Desktop JSON export without changing the ERP database
or publishing anything to Telegram. It produces an idempotent pilot manifest and
a separate manual-review manifest.

```powershell
dotnet run --project tools/TelegramSalesListImport -- `
  "C:\Users\pascal\Downloads\Telegram Desktop\ChatExport_2026-09-01\result.json" `
  "output\telegram-sales-list-import" `
  20
```

The source channel ID plus source message ID is the stable import identity. The
next import stage must reject a duplicate pair before creating a review message.
