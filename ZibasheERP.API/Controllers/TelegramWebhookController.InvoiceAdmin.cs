using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private async Task<bool> TryHandleAdminCommandAsync(TelegramMessage message, CancellationToken ct)
    {
        var text = message.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text) ||
            !(text.StartsWith("/admin", StringComparison.OrdinalIgnoreCase) ||
              text.StartsWith("/bank", StringComparison.OrdinalIgnoreCase)))
            return false;

        if (!await IsAuthorizedInvoiceAdminAsync(message.Chat.Id, message.From!.Id, ct))
        {
            await ReplyAsync(message.Chat.Id, "این بخش فقط برای مدیران گروه حسابداری فعال است.", ct);
            return true;
        }

        if (text.StartsWith("/bankadd ", StringComparison.OrdinalIgnoreCase))
        {
            var values = text[9..].Split('|', StringSplitOptions.TrimEntries);
            if (values.Length != 3 || !TryNormalizeCard(values[0], out var card))
            {
                await ReplyAsync(message.Chat.Id, "فرمت صحیح:\n/bankadd شماره‌کارت | نام صاحب حساب | نام بانک", ct);
                return true;
            }
            var accounts = await _paymentAccountRepository.GetForAdminAsync(ct);
            if (accounts.Count >= 4)
            {
                await ReplyAsync(message.Chat.Id, "حداکثر ۴ حساب بانکی قابل ثبت است. ابتدا یکی از حساب‌های قبلی را حذف کنید.", ct);
                return true;
            }
            await _paymentAccountRepository.AddAsync(new InvoicePaymentAccount
            {
                Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, CardNumber = card,
                AccountHolder = values[1], BankName = values[2],
                DisplayOrder = accounts.Count, IsActive = true
            }, ct);
            await _paymentAccountRepository.SaveChangesAsync(ct);
            await SendInvoiceAdminMenuAsync(message.Chat.Id, "حساب بانکی اضافه شد ✅", ct);
            return true;
        }

        await SendInvoiceAdminMenuAsync(message.Chat.Id, null, ct);
        return true;
    }

    private async Task<bool> TryHandleAdminCallbackAsync(TelegramCallbackQuery callback, CancellationToken ct)
    {
        if (callback.Message is null || callback.Data is null ||
            !callback.Data.StartsWith("invoiceadmin:", StringComparison.Ordinal))
            return false;
        if (!await IsAuthorizedInvoiceAdminAsync(callback.Message.Chat.Id, callback.From.Id, ct))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "دسترسی مدیریت ندارید.", ct);
            return true;
        }

        var parts = callback.Data.Split(':');
        if (parts.Length == 3 && Guid.TryParseExact(parts[2], "N", out var id))
        {
            var account = await _paymentAccountRepository.GetByIdAsync(id, ct);
            if (account is not null)
            {
                if (parts[1] == "toggle") account.IsActive = !account.IsActive;
                if (parts[1] == "delete") account.IsDeleted = true;
                account.UpdatedAt = DateTime.UtcNow;
                await _paymentAccountRepository.SaveChangesAsync(ct);
            }
        }
        await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
        await SendInvoiceAdminMenuAsync(callback.Message.Chat.Id, null, ct);
        return true;
    }

    private async Task SendInvoiceAdminMenuAsync(long chatId, string? notice, CancellationToken ct)
    {
        var accounts = await _paymentAccountRepository.GetForAdminAsync(ct);
        var lines = accounts.Count == 0
            ? "هنوز حساب بانکی ثبت نشده است."
            : string.Join("\n\n", accounts.Select((x, i) =>
                $"{i + 1}. {(x.IsActive ? "✅" : "⛔")} {FormatCard(x.CardNumber)}\n{x.AccountHolder} — بانک {x.BankName}"));
        var buttons = accounts.SelectMany(x => new IReadOnlyCollection<TelegramInlineButton>[]
        {
            new[] { new TelegramInlineButton(x.IsActive ? "غیرفعال‌کردن" : "فعال‌کردن", $"invoiceadmin:toggle:{x.Id:N}"),
                    new TelegramInlineButton("حذف", $"invoiceadmin:delete:{x.Id:N}") }
        }).Append((IReadOnlyCollection<TelegramInlineButton>)new[]
        {
            new TelegramInlineButton("➕ راهنمای افزودن حساب", "invoiceadmin:add")
        }).ToArray();
        var message = (notice is null ? "" : notice + "\n\n") +
            $"⚙️ تنظیمات فاکتور زیباشی\n⏱ مهلت پرداخت: ۲۴ ساعت\n🏦 حساب‌ها: {accounts.Count}/4 (پیشنهاد: ۲ حساب فعال)\n\nحساب‌های بانکی:\n" + lines +
            "\n\nافزودن حساب:\n/bankadd شماره‌کارت | نام صاحب حساب | نام بانک";
        await _sender.SendInlineKeyboardAsync(chatId.ToString(), message, buttons, ct);
    }

    private async Task<bool> IsAuthorizedInvoiceAdminAsync(long chatId, long userId, CancellationToken ct) =>
        long.TryParse(_options.AdminChatId, out var configured) && configured == chatId &&
        await _sender.IsChatAdministratorAsync(chatId.ToString(), userId.ToString(), ct);

    private static bool TryNormalizeCard(string value, out string card)
    {
        card = new string(value.Where(char.IsDigit).ToArray());
        return card.Length == 16;
    }

    private static string FormatCard(string card) => string.Join('-', Enumerable.Range(0, 4).Select(i => card.Substring(i * 4, 4)));
}
