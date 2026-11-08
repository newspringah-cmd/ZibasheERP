using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZibasheERP.API.Marketing;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Controllers;

[ApiController]
[Route("api/marketing-promotions")]
[Authorize(Roles = "Admin")]
public sealed class MarketingPromotionsController(INotificationOutboxRepository repository, ITelegramMessageSender sender,AppDbContext? context=null) : ControllerBase
{
    [HttpGet("pending")]
    public async Task<IActionResult> Pending(CancellationToken ct,[FromQuery]string? lane=null)
    {
        if(lane is not(null or "regular" or "inventory"))return BadRequest();
        IReadOnlyCollection<NotificationOutbox> jobs;
        if(context is null)jobs=await repository.GetPendingAsync(MarketingPromotionJob.Channel,1,ct);
        else
        {
            var inventory=lane=="inventory";var now=DateTime.UtcNow;
            await using var tx=await context.Database.BeginTransactionAsync(ct);
            jobs=await context.NotificationOutbox.FromSqlInterpolated($"""
                SELECT TOP(1) * FROM NotificationOutbox WITH(UPDLOCK,READPAST,ROWLOCK)
                WHERE IsDeleted=0 AND Channel=N'MarketingPromotion'
                  AND (({inventory}=1 AND EventType=N'ReadyInventoryPublication') OR ({inventory}=0 AND EventType<>N'ReadyInventoryPublication'))
                  AND ((Status=1 AND (NextAttemptAt IS NULL OR NextAttemptAt<={now})) OR (Status=4 AND LockedUntil<{now}))
                ORDER BY CreatedAt
                """).ToArrayAsync(ct);
            foreach(var job in jobs){job.Status=NotificationOutboxStatus.Processing;job.UpdatedAt=now;job.LockedUntil=now.AddMinutes(20);}
            await context.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        }
        foreach (var job in jobs) job.LockedUntil = DateTime.UtcNow.AddMinutes(20);
        await repository.SaveChangesAsync(ct);
        return Ok(jobs.Select(value => new { value.Id, value.Payload, value.Attempts, value.CreatedAt }));
    }

    public sealed record ResultRequest(string State, string? Url = null, string? Error = null);

    [HttpPost("{id:guid}/result")]
    public async Task<IActionResult> Result(Guid id, ResultRequest request, CancellationToken ct)
    {
        if (request.State is not ("sent" or "retry" or "failed" or "unknown")) return BadRequest();
        var job = await repository.GetByIdAsync(id, ct);
        if (job is null || job.IsDeleted || job.Channel != MarketingPromotionJob.Channel) return NotFound();
        if (job.Status is NotificationOutboxStatus.Processed or NotificationOutboxStatus.Failed) return NoContent();
        if (job.Status != NotificationOutboxStatus.Processing) return Conflict();
        var payload = JsonSerializer.Deserialize<MarketingPromotionJob>(job.Payload);
        if (payload is null) return BadRequest();
        if (request.State == "sent" && (payload.Destination is MarketingPromotionJob.RemoveChannel or MarketingPromotionJob.FlashEnd or "inventory-publish" or "inventory-report"
            ? request.Url is not null : !ValidResultUrl(request.Url, payload.Destination))) return BadRequest();
        job.Attempts++;
        job.UpdatedAt = DateTime.UtcNow;
        job.LockedUntil = null;
        var retry = request.State == "retry" && job.Attempts < 5;
        job.Status = request.State == "sent" ? NotificationOutboxStatus.Processed :
            retry ? NotificationOutboxStatus.Pending : NotificationOutboxStatus.Failed;
        job.NextAttemptAt = retry ? DateTime.UtcNow.AddSeconds(60) : null;
        job.ProcessedAt = request.State == "sent" ? DateTime.UtcNow : null;
        job.LastError = request.State == "sent" ? null :
            request.State == "unknown" ? "نتیجهٔ ارسال نامشخص است؛ برای جلوگیری از تبلیغ تکراری نیازمند بررسی مدیر است." :
            string.IsNullOrWhiteSpace(request.Error) ? "ارسال انجام نشد؛ وضعیت لیست و عکس‌ها را بررسی کنید." : request.Error[..Math.Min(500, request.Error.Length)];
        if (request.State == "sent") job.Payload = JsonSerializer.Serialize(payload with { ResultUrl = request.Url });
        await repository.SaveChangesAsync(ct);
        if (retry) return NoContent();
        var title = payload.Destination == "inventory-publish" ? $"انتشار موجودی {payload.Code}" : payload.Destination == "inventory-report" ? $"گزارش واریز موجودی {payload.Code}" : payload.Destination == MarketingPromotionJob.RemoveChannel ? "حذف تبلیغ کانال" :
            payload.Destination == MarketingPromotionJob.GroupMessage ? "ارسال پیام به گروه" :
            payload.Destination == MarketingPromotionJob.FlashStart ? "شروع پروموشن ساعتی" :
            payload.Destination == MarketingPromotionJob.FlashEnd ? "پایان پروموشن ساعتی" : $"تبلیغ لیست {payload.Code}";
        var message = request.State == "sent" && payload.Destination == MarketingPromotionJob.RemoveChannel
            ? "✅ تبلیغ‌های کانال حذف شدند و ارسال خودکار کانال متوقف است. برای شروع دوباره، «تبلیغ در کانال» را انتخاب کنید و کد لیست را بفرستید."
            : request.State == "sent" && payload.Destination == "inventory-publish"
            ? $"✅ موجودی {payload.Code} در کانال موجودی منتشر شد.\nاین پست محدودیت زمانی ندارد و تا فروش و صدور فاکتور باقی می‌ماند."
            : request.State == "sent" && payload.Destination == "inventory-report"
            ? $"✅ گزارش فاکتور موجودی {payload.Code} به گروه واریز جدید ارسال شد."
            : request.State == "sent" && payload.Destination == MarketingPromotionJob.FlashEnd
            ? "✅ پروموشن پایان یافت و پیام تشکر در گروه و کانال ارسال شد. تخفیف آیتم‌های قبلی در فاکتور حفظ می‌شود."
            : request.State == "sent" && payload.Destination == MarketingPromotionJob.FlashStart
            ? $"✅ پروموشن لیست {payload.Code} در گروه و کانال فعال شد.\n{request.Url}"
            : request.State == "sent" && payload.Destination == MarketingPromotionJob.GroupMessage
            ? $"✅ پیام شما از طرف زیباشی در گروه ارسال شد.\n{request.Url}"
            : request.State == "sent" ?
            $"✅ تبلیغ لیست {payload.Code} در {(payload.Destination == "group" ? "گروه" : "کانال — ۲۴ ساعت")} ارسال شد.\n{request.Url}" :
            $"⚠️ {title}\n{job.LastError}";
        await sender.SendInlineKeyboardAsync(payload.AdminChatId.ToString(System.Globalization.CultureInfo.InvariantCulture), message,
            [[new TelegramInlineButton("📋 وضعیت درخواست", payload.Destination is MarketingPromotionJob.FlashStart or MarketingPromotionJob.FlashEnd ? $"invoiceadmin:flash:status:{id:N}" : $"invoiceadmin:marketing:status:{id:N}")],
             [new TelegramInlineButton("↩️ مدیریت", "invoiceadmin:menu:main")]], ct);
        return NoContent();
    }

    public static bool ValidResultUrl(string? url, string destination)
    {
        var prefix = destination is "group" or MarketingPromotionJob.GroupMessage or MarketingPromotionJob.FlashStart ? "https://t.me/c/1283018740/" :
            destination == "channel" ? "https://t.me/zibasheperfume/" : "";
        return prefix.Length > 0 && url is not null && url.StartsWith(prefix, StringComparison.Ordinal) &&
            long.TryParse(url[prefix.Length..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0;
    }
}
