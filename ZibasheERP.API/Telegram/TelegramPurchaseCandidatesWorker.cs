using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Telegram;

public sealed class TelegramPurchaseCandidatesWorker(IServiceScopeFactory scopes,
    ITelegramMessageSender sender, ILogger<TelegramPurchaseCandidatesWorker> logger) : BackgroundService
{
    private readonly Channel<(long Chat, Guid Session, int Page)> jobs = Channel.CreateBounded<(long, Guid, int)>(10);
    private readonly ConcurrentDictionary<Guid, (long Chat, Guid[] Ids, DateTime Expires)> sessions = new();
    private readonly ConcurrentDictionary<long, byte> busy = new();

    public async Task<bool> StartAsync(long chat, CancellationToken ct)
    {
        if (!busy.TryAdd(chat, 0)) return false;
        try
        {
            foreach (var entry in sessions.Where(x => x.Value.Expires < DateTime.UtcNow))
                sessions.TryRemove(entry.Key, out _);
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ids = await db.SalesLists.AsNoTracking()
                .Where(x => !x.IsDeleted && !x.IsInventoryOffer && x.Status == SalesListStatus.Open &&
                    x.TotalVolume > 0 && x.ReservedVolume < x.TotalVolume && x.ReservedVolume * 100 > x.TotalVolume * 70)
                .OrderByDescending(x => (decimal)x.ReservedVolume / x.TotalVolume)
                .ThenBy(x => x.OpenDate).ThenBy(x => x.Id).Select(x => x.Id).ToArrayAsync(ct);
            var id = Guid.NewGuid();
            sessions[id] = (chat, ids, DateTime.UtcNow.AddHours(2));
            if (jobs.Writer.TryWrite((chat, id, 0))) return true;
            sessions.TryRemove(id, out _);
            busy.TryRemove(chat, out _);
            return false;
        }
        catch { busy.TryRemove(chat, out _); throw; }
    }

    public bool QueuePage(long chat, Guid session, int page)
    {
        if (!sessions.TryGetValue(session, out var state) || state.Chat != chat ||
            state.Expires < DateTime.UtcNow || page < 0 || page * 50L >= state.Ids.Length || !busy.TryAdd(chat, 0))
            return false;
        if (jobs.Writer.TryWrite((chat, session, page))) return true;
        busy.TryRemove(chat, out _);
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in jobs.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var state = sessions[job.Session];
                var ids = state.Ids.Skip(job.Page * 50).Take(50).ToArray();
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var lists = await db.SalesLists.AsNoTracking().Where(x => ids.Contains(x.Id) && !x.IsDeleted &&
                    !x.IsInventoryOffer && x.Status == SalesListStatus.Open && x.TotalVolume > 0 &&
                    x.ReservedVolume < x.TotalVolume && x.ReservedVolume * 100 > x.TotalVolume * 70)
                    .ToDictionaryAsync(x => x.Id, stoppingToken);
                foreach (var id in ids)
                {
                    if (!lists.TryGetValue(id, out var list)) continue;
                    var name = System.Net.WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(list.PersianName)
                        ? list.EnglishName : list.PersianName);
                    var caption = $"<b>{name}</b>\nکد: {list.DisplayCode}\nتکمیل: {(decimal)list.ReservedVolume * 100 / list.TotalVolume:0.#}٪\nباقی‌مانده: {list.RemainingVolume} میل";
                    var buttons = new List<IReadOnlyCollection<TelegramInlineButton>>();
                    if (list.TelegramMessageId.HasValue && !string.IsNullOrWhiteSpace(list.TelegramChannelId))
                        buttons.Add(new[] { new TelegramInlineButton("🔗 مشاهده لیست", Url:
                            $"https://t.me/c/{list.TelegramChannelId.Replace("-100", "")}/{list.TelegramMessageId}") });
                    else if (Uri.TryCreate(list.ProductPageUrl, UriKind.Absolute, out var url) &&
                        (url.Scheme == "https" || url.Scheme == "http"))
                        buttons.Add(new[] { new TelegramInlineButton("🔗 صفحه عطر", Url: list.ProductPageUrl) });
                    var result = string.IsNullOrWhiteSpace(list.TelegramPhotoFileId)
                        ? await sender.SendInlineKeyboardAsync(job.Chat.ToString(),
                            System.Net.WebUtility.HtmlDecode(caption.Replace("<b>", "").Replace("</b>", "")), buttons, stoppingToken)
                        : await sender.SendPhotoWithKeyboardAsync(job.Chat.ToString(), list.TelegramPhotoFileId,
                            caption, buttons, stoppingToken);
                    if (!result.IsSuccessful) throw new InvalidOperationException(result.Error);
                    await Task.Delay(3200, stoppingToken);
                }
                var pages = Math.Max(1, (state.Ids.Length + 49) / 50);
                var navigation = new List<IReadOnlyCollection<TelegramInlineButton>>();
                if ((job.Page + 1) * 50 < state.Ids.Length)
                    navigation.Add(new[] { new TelegramInlineButton("صفحه بعد ➡️", $"purchasepage:{job.Session:N}:{job.Page + 1}") });
                await sender.SendInlineKeyboardAsync(job.Chat.ToString(),
                    state.Ids.Length == 0 ? "لیست باز با بیش از ۷۰٪ تکمیل وجود ندارد." :
                    $"صفحه {job.Page + 1} از {pages} — نمایش {lists.Count} لیست\nکل در زمان گزارش: {state.Ids.Length}", navigation, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                logger.LogError(error, "Purchase candidates report failed for chat {Chat}", job.Chat);
                await sender.SendAsync(job.Chat.ToString(), "ارسال گزارش کامل نشد؛ دوباره /r را اجرا کنید.", stoppingToken);
            }
            finally { busy.TryRemove(job.Chat, out _); }
        }
    }
}
