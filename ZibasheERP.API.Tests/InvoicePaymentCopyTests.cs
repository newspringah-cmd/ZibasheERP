using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;

public static class InvoicePaymentCopyTests
{
    public static async Task Run(Action<bool,string> check)
    {
        IReadOnlyCollection<TelegramInlineButton>[] Buttons(decimal amount) =>
            (IReadOnlyCollection<TelegramInlineButton>[])typeof(TelegramOutboxWorker)
                .GetMethod("BuildNotificationButtons", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, ["InvoiceIssued", JsonSerializer.Serialize(new { TotalAmount = amount })])!;
        var button = Buttons(805420m).SelectMany(r => r).Single();
        check(button.Text == "📋 کپی مبلغ: 8054200 ریال" && button.CopyText == "8054200", "invoice button displays rials and copies only plain ASCII bank-ready digits");
        check(Buttons(0).Length == 0, "zero-payable invoices do not request a bank transfer");
        check(Buttons(150000m).SelectMany(r => r).Single().CopyText == "1500000", "invoice amount button uses the actual final total rather than perfume unit price");
        var fields = typeof(TelegramMessageSender).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var sender = new TelegramMessageSender(Options.Create(new TelegramOptions { BotToken = "fake-token" }));
        ((HttpClient)fields.GetValue(sender)!).Dispose();
        var handler = new PaymentCopyHandler(); fields.SetValue(sender, new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/") });
        const string caption = "🧾 فاکتور\nمبلغ قابل پرداخت: ۸۰۵٬۴۲۰ تومان";
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows = new[] { new[] { button } };
        await sender.SendInlineKeyboardAsync("-100123", caption, rows);
        await sender.SendDocumentWithKeyboardAsync("-100123", new byte[] { 1 }, "invoice.pdf", caption, rows);
        foreach (var values in handler.Fields)
        {
            using var keyboard = JsonDocument.Parse(values["reply_markup"]);
            check(keyboard.RootElement.GetProperty("inline_keyboard")[0][0].GetProperty("copy_text").GetProperty("text").GetString() == "8054200", "Telegram native copy_text button copies the rial amount on text and PDF invoices");
            check((values.TryGetValue("caption", out var c) ? c : values["text"]) == caption, "rial amount is added only to the keyboard and leaves the invoice caption unchanged");
        }

        var connection = Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var cs = new SqlConnectionStringBuilder(connection);
        if (cs.InitialCatalog != "ZibasheERPCloudDev" || cs.DataSource != "127.0.0.1,14333") throw new InvalidOperationException("Isolated development database required");
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        var customer = new Customer { Id = Guid.NewGuid(), FullName = "Rial button fixture", Mobile = Guid.NewGuid().ToString("N")[..18], CreatedAt = DateTime.UtcNow };
        var order = new Order { Id = Guid.NewGuid(), CustomerId = customer.Id, OrderNumber = "RIAL-" + Guid.NewGuid().ToString("N"), Status = OrderStatus.Invoiced, CreatedAt = DateTime.UtcNow };
        var invoice = new Invoice { Id = Guid.NewGuid(), OrderId = order.Id, InvoiceNumber = "INV-RIAL-" + Guid.NewGuid().ToString("N"), TotalAmount = 805420m, Status = InvoiceStatus.Issued, CreatedAt = DateTime.UtcNow, IssuedAt = DateTime.UtcNow };
        var job = new NotificationOutbox { Id = Guid.NewGuid(), CustomerId = customer.Id, OrderId = order.Id, Channel = "N8n", EventType = "InvoiceIssued", Recipient = "test", CreatedAt = DateTime.UtcNow };
        void Payload(string? role = null) => job.Payload = JsonSerializer.Serialize(new { InvoiceId = invoice.Id, TotalAmount = 123m, GiftDeliveryRole = role, Delivery = new { Channel = "TelegramGroup", ChatId = "-100123" } });
        Payload(); db.Customers.Add(customer); db.Orders.Add(order); db.Invoices.Add(invoice); db.NotificationOutbox.Add(job);
        var controller = new N8nIntegrationsController(null!, db, Options.Create(new TelegramOptions()), sender);
        async Task<IActionResult> Deliver(string text)
        {
            using var stream = new MemoryStream(new byte[] { 1 });
            var document = new FormFile(stream, 0, 1, "document", "invoice.pdf");
            return await controller.SendTelegramInvoice(job.Id, "-100123", text, "invoice.pdf", document, default);
        }
        try
        {
            await db.SaveChangesAsync();
            foreach (var inventory in new[] { false, true })
            {
                order.IsInventory = inventory; await db.SaveChangesAsync();
                check(await Deliver(caption) is OkObjectResult, "PDF invoice sends with a rial copy button for ordinary and ready inventory orders");
                var request = handler.Fields.Last(); using var keyboard = JsonDocument.Parse(request["reply_markup"]);
                var copy = keyboard.RootElement.GetProperty("inline_keyboard").EnumerateArray().SelectMany(r => r.EnumerateArray()).Single(b => b.TryGetProperty("copy_text", out _));
                check(copy.GetProperty("copy_text").GetProperty("text").GetString() == "8054200", "PDF delivery reads authoritative persisted invoice total even when the retry event contains a different amount");
                check(request["caption"] == (inventory ? "🔴🔴موجودی🔴🔴\n" : "") + caption, "PDF caption keeps its existing text and inventory heading without a rial amount line");
            }
            Payload("Recipient"); await db.SaveChangesAsync(); await Deliver(caption);
            using (var keyboard = JsonDocument.Parse(handler.Fields.Last()["reply_markup"]))
                check(!keyboard.RootElement.GetProperty("inline_keyboard").EnumerateArray().SelectMany(r => r.EnumerateArray()).Any(b => b.TryGetProperty("copy_text", out _)), "gift recipient PDF does not expose the giver's payable amount button");
            invoice.Status = InvoiceStatus.Cancelled; await db.SaveChangesAsync(); var count = handler.Fields.Count;
            check(await Deliver(caption) is ConflictObjectResult && handler.Fields.Count == count, "cancelled invoice guard still prevents PDF delivery");
        }
        finally
        {
            db.ChangeTracker.Clear(); await db.NotificationOutbox.Where(n => n.OrderId == order.Id).ExecuteDeleteAsync();
            await db.Invoices.Where(i => i.Id == invoice.Id).ExecuteDeleteAsync(); await db.Orders.Where(o => o.Id == order.Id).ExecuteDeleteAsync(); await db.Customers.Where(c => c.Id == customer.Id).ExecuteDeleteAsync();
        }
    }
}

public sealed class PaymentCopyHandler : HttpMessageHandler
{
    public List<Dictionary<string,string>> Fields = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var values = new Dictionary<string,string>();
        if (request.Content is MultipartFormDataContent multipart)
        {
            foreach (var part in multipart)
            {
                var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                if (name != "document") values[name] = await part.ReadAsStringAsync(ct);
            }
        }
        else
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            foreach (var p in json.RootElement.EnumerateObject()) values[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
        }
        Fields.Add(values);
        return new(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":123,\"document\":{\"file_id\":\"fake-pdf\"}}}") };
    }
}
