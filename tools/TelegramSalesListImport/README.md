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

To reprocess only messages from an earlier manifest against a newer Telegram
export, pass that manifest as the fifth argument:

```powershell
dotnet run --project tools/TelegramSalesListImport -- `
  "C:\new-export\result.json" `
  "output\manual-review-refresh" `
  10000 `
  0 `
  "migration-batch-001\manual-review-manifest.json"
```

The filter uses Telegram source message IDs. New and re-published channel posts
are excluded, and the summary reports any filtered messages missing from the
new export.
