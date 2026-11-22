using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Marketing;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private static string InventoryPackageQuantity(SalesList list) =>
        list.IsMiscellaneousInventory ? "۱ عدد" : $"{list.TotalVolume:0.#} میل";

    private async Task<bool> TryHandleMiscellaneousInventoryCallbackAsync(TelegramCallbackQuery callback,
        string action, CancellationToken ct)
    {
        var chat = callback.Message!.Chat.Id;
        var user = callback.From.Id;
        var key = (chat, user);
        if (action == "misc")
        {
            ClearAdminWorkflowDrafts(chat, user);
            InventoryDrafts[key] = new(Guid.NewGuid(), "misc-details", DateTime.UtcNow.AddMinutes(30));
            await InvPrompt(chat, "نام کالا و قیمت کل را در یک پیام دوخطی بفرستید.\nخط اول: نام کالا\nخط دوم: قیمت به تومان", ct);
            return true;
        }
        const string confirm = "misc-save:";
        if (!action.StartsWith(confirm, StringComparison.Ordinal)) return false;

        var gate = ReservationStartLocks[(int)((uint)HashCode.Combine(chat, user) % (uint)ReservationStartLocks.Length)];
        await gate.WaitAsync(ct);
        try
        {
            if (!InventoryDrafts.TryGetValue(key, out var draft) || draft.Step != "misc-confirm" ||
                draft.Token != action[confirm.Length..] || draft.Until < DateTime.UtcNow)
                throw new InvalidOperationException("این پیش‌نمایش منقضی یا قبلاً ثبت شده است.");
            if (string.IsNullOrWhiteSpace(_options.ReadyInventoryChannelId))
                throw new InvalidOperationException("مقصد کانال موجودی تنظیم نشده است.");

            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var list = await new ReadyInventoryService(_db).CreateAsync(new(draft.Id, null, draft.Name,
                draft.Photo, draft.Price, 1, SalesList.MiscellaneousInventoryPackingId, 0, string.Empty, user), ct);
            await QueueFlashAsync(list.Id, new MarketingPromotionJob(list.Id, list.DisplayCode, "inventory-publish", chat, user), ct);
            await transaction.CommitAsync(ct);
            InventoryDrafts.TryRemove(key, out _);
            await InvPrompt(chat, $"✅ موجودی متفرقه با کد {list.DisplayCode} ثبت شد و در صف انتشار کانال موجودی است.", ct);
            return true;
        }
        finally { gate.Release(); }
    }

    private async Task<bool> HandleMiscellaneousInventoryMessageAsync(TelegramMessage message, InventoryDraft draft,
        string text, CancellationToken ct)
    {
        var chat = message.Chat.Id;
        var key = (chat, message.From!.Id);
        if (draft.Step == "misc-details")
        {
            var lines = text.Split('\n', StringSplitOptions.TrimEntries);
            var priceText = lines.Length == 2 ? lines[1] : string.Empty;
            if (priceText.EndsWith("تومان", StringComparison.Ordinal)) priceText = priceText[..^5].TrimEnd();
            if (lines.Length != 2 || lines[0].Length is <1 or >200 ||
                !long.TryParse(NormalizeNumber(priceText), NumberStyles.None, CultureInfo.InvariantCulture, out var price) ||
                price is <=0 or >1000000000000L)
                throw new InvalidOperationException("در خط اول نام کالا و در خط دوم قیمت کل به تومان را بفرستید.");
            InventoryDrafts[key] = draft with { Name = lines[0], Price = price, Step = "misc-photo" };
            await InvPrompt(chat, "عکس کالا را به صورت عکس بفرستید.", ct);
            return true;
        }
        if (draft.Step == "misc-photo")
        {
            var photo = message.Photo?.MaxBy(value => (long)value.Width * value.Height);
            if (photo is null) throw new InvalidOperationException("عکس کالا را به صورت عکس بفرستید.");
            var token = Guid.NewGuid().ToString("N")[..12];
            var sent = await _sender.SendPhotoWithKeyboardAsync(chat.ToString(), photo.FileId,
                $"👁 پیش‌نمایش موجودی متفرقه\n<b>{Html(draft.Name)}</b>\n📦 تعداد: ۱ عدد\n💰 قیمت: {draft.Price:N0} تومان",
                [[new TelegramInlineButton("✅ تأیید و انتشار", InvAction("misc-save:" + token))],
                 [new TelegramInlineButton("❌ انصراف", InvAction("menu"))]], ct);
            if (!sent.IsSuccessful) throw new InvalidOperationException("پیش‌نمایش عکس ارسال نشد؛ عکس را دوباره بفرستید.");
            InventoryDrafts.TryUpdate(key, draft with { Step = "misc-confirm", Photo = photo.FileId, Token = token }, draft);
            return true;
        }
        await InvPrompt(chat, "برای انتشار، دکمهٔ تأیید زیر پیش‌نمایش را بزنید.", ct);
        return true;
    }
}
