using System.Text;
using ZibasheERP.API.AddressLabels;

namespace ZibasheERP.API.Tracking;

public static class PreparedAddressReport
{
    public const int PageSize = 50;
    public sealed record Entry(long MessageId, string Name, string Username, DateTime? RegisteredAt = null);
    public sealed record Page(string Text, int Index, int Pages)
    {
        public string[] Messages
        {
            get
            {
                var chunks=new List<string>();var current=new StringBuilder();
                // Keep each complete entry (name, date and address link) together.
                foreach(var block in Text.Split("\n\n",StringSplitOptions.None))
                {
                    if(current.Length>0&&current.Length+block.Length+2>3700){chunks.Add(current.ToString());current.Clear();}
                    if(current.Length>0)current.Append("\n\n");current.Append(block);
                }
                if(current.Length>0)chunks.Add(current.ToString());
                return chunks.Select((chunk,i)=>chunks.Count==1?chunk+"\n":$"📄 صفحهٔ {Index+1} از {Pages} — بخش {i+1} از {chunks.Count}\n\n"+chunk+"\n").ToArray();
            }
        }
    }

    public static Page Build(string chat, IReadOnlyCollection<Entry> entries, int requestedPage)
    {
        var rows = entries.Where(r => r.MessageId > 0).DistinctBy(r => r.MessageId).ToArray();
        var pages = Math.Max(1, (rows.Length + PageSize - 1) / PageSize);
        var index = Math.Clamp(requestedPage, 0, pages - 1);
        var text = new StringBuilder($"📮 آدرس‌های باقی‌مانده آمادهٔ ارسال\n\nتعداد آدرس‌های ثبت‌شده و ارسال‌نشده: {rows.Length}\n");
        if (rows.Length == 0) text.Append("\n✅ آدرس آمادهٔ ارسالِ باقی‌مانده‌ای ثبت نشده است.\n");
        foreach (var (row, offset) in rows.Skip(index * PageSize).Take(PageSize).Select((r, i) => (r, i)))
        {
            static string Limit(string value, int length) => value.Length > length ? value[..length] + "…" : value;
            var name = Limit(ShippingAddressDisplay.NormalizeName(row.Name), 100);
            var username = Limit(row.Username.Trim().TrimStart('@'), 64);
            text.Append($"\n{index * PageSize + offset + 1}. {name}");
            if (username.Length > 0) text.Append(" — @" + username);
            if(row.RegisteredAt is DateTime registered) text.Append("\n📅 تاریخ ثبت: "+ShippingAddressDisplay.RegisteredDate(registered));
            text.Append('\n').Append(chat.StartsWith("-100", StringComparison.Ordinal)
                ? $"https://t.me/c/{chat[4..]}/{row.MessageId}" : $"پیام آدرس: {row.MessageId}").Append('\n');
        }
        text.Append($"\nصفحهٔ {index + 1} از {pages}\nهر آدرس یک بسته است؛ پست مشترک یک‌بار شمرده می‌شود.\nوضعیت در زمان بازکردن یا به‌روزرسانی گزارش محاسبه می‌شود.");
        return new(text.ToString(), index, pages);
    }
}
