using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace ZibasheERP.Infrastructure.Services;

public sealed record AlanchandDirhamQuote(decimal TomanPerDirham, string SourceUnit,
    string PriceColumn, string SourcePriceText, DateTime FetchedAtUtc, string DocumentSha256);

public sealed class AlanchandDirhamRateSource(HttpClient client)
{
    public const string SourceUrl = "https://alanchand.com/currencies-price";
    private const int MaximumBytes = 2 * 1024 * 1024;

    public async Task<AlanchandDirhamQuote> FetchAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, SourceUrl);
        request.Headers.Accept.ParseAdd("text/html");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var uri = response.RequestMessage?.RequestUri;
        if (uri is null || uri.Scheme != "https" ||
            (uri.Host != "alanchand.com" && uri.Host != "www.alanchand.com"))
            throw new InvalidOperationException("Unexpected exchange-rate source.");
        if (response.Content.Headers.ContentType?.MediaType is not ("text/html" or "application/xhtml+xml"))
            throw new InvalidOperationException("Exchange-rate source did not return HTML.");
        if (response.Content.Headers.ContentLength > MaximumBytes)
            throw new InvalidOperationException("Exchange-rate document too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int length;
        while ((length = await stream.ReadAsync(chunk, ct)) != 0)
        {
            if (buffer.Length + length > MaximumBytes)
                throw new InvalidOperationException("Exchange-rate document too large.");
            await buffer.WriteAsync(chunk.AsMemory(0, length), ct);
        }
        return Parse(Encoding.UTF8.GetString(buffer.ToArray()), DateTime.UtcNow);
    }

    // Never infer a price from unrelated page numbers or an unlabeled currency/unit.
    public static AlanchandDirhamQuote Parse(string html, DateTime fetchedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(html) || Encoding.UTF8.GetByteCount(html) > MaximumBytes)
            throw new InvalidOperationException("Invalid exchange-rate document.");
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        foreach (var node in doc.DocumentNode.Descendants()
                     .Where(n => n.Name is "script" or "style" or "noscript" || n.Attributes["hidden"] is not null)
                     .ToArray()) node.Remove();
        var quotes = new List<AlanchandDirhamQuote>();
        foreach (var table in doc.DocumentNode.Descendants("table"))
        {
            var allRows = table.Descendants("tr").Where(r => r.Ancestors("table").First() == table).ToArray();
            var header = allRows.FirstOrDefault(r => r.Elements("th").Any());
            if (header is null) continue;
            var columns = Cells(header).Select(Text).ToArray();
            var sell = Array.FindAll(Enumerable.Range(0, columns.Length).ToArray(), i => columns[i].Contains("فروش"));
            var selected = sell.Length == 1 ? sell[0] : sell.Length == 0
                ? Array.FindIndex(columns, c => c.Contains("قیمت") || c.Contains("نرخ فعلی")) : -1;
            if (selected < 0) continue;
            foreach (var row in allRows.Where(r => r != header))
            {
                var cells = Cells(row);
                if (cells.Length != columns.Length || !cells.Any(IsDirham)) continue;
                var text = Text(cells[selected]);
                var labels = columns[selected] + " " + text;
                var toman = labels.Contains("تومان");
                var rial = labels.Contains("ریال");
                if (toman == rial || !TryPrice(text, out var amount))
                    throw new InvalidOperationException("Dirham amount or currency unit is ambiguous.");
                var value = rial ? amount / 10m : amount;
                if (value <= 0 || value > 1_000_000_000m || decimal.Round(value, 4) != value)
                    throw new InvalidOperationException("Invalid dirham rate.");
                quotes.Add(new(value, rial ? "IRR" : "Toman", columns[selected], text,
                    DateTime.SpecifyKind(fetchedAtUtc, DateTimeKind.Utc),
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html)))));
            }
        }
        if (quotes.Count == 0 || quotes.Select(q => q.TomanPerDirham).Distinct().Count() != 1)
            throw new InvalidOperationException("A unique labeled AED exchange rate was not found.");
        return quotes[0];
    }

    private static HtmlNode[] Cells(HtmlNode row) => row.ChildNodes.Where(n => n.Name is "td" or "th").ToArray();
    private static string Text(HtmlNode node) => Normalize(HtmlEntity.DeEntitize(node.InnerText));
    private static bool IsDirham(HtmlNode node)
    {
        var text = Text(node);
        return (text.Contains("درهم") && text.Contains("امارات")) ||
               Regex.IsMatch(text, @"\bAED\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    }
    private static string Normalize(string text)
    {
        var chars = text.Replace('ي', 'ی').Replace('ك', 'ک').Select(c => c is >= '۰' and <= '۹'
            ? (char)('0' + c - '۰') : c is >= '٠' and <= '٩' ? (char)('0' + c - '٠') : c).ToArray();
        return new string(chars).Replace('\u200c', ' ').Replace('\u200e', ' ').Replace('\u200f', ' ').Trim();
    }
    private static bool TryPrice(string text, out decimal value)
    {
        text = Normalize(WebUtility.HtmlDecode(text)).Replace("تومان", "").Replace("ریال", "")
            .Replace("٬", "").Replace(",", "").Replace("٫", ".").Replace(" ", "").Replace("\u00a0", "");
        return decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }
}
