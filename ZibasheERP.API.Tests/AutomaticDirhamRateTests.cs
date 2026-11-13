using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Services;

public static class AutomaticDirhamRateTests
{
    private static string Table(string unit, string price, string currency = "درهم امارات", string buy = "۳۹٬۰۰۰") =>
        $"<table><thead><tr><th>ارز</th><th>خرید ({unit})</th><th>فروش ({unit})</th></tr></thead>"+
        $"<tbody><tr><td>{currency}</td><td>{buy}</td><td>{price}</td></tr></tbody></table>";

    public static async Task Run(Action<bool,string> check)
    {
        var at = new DateTime(2026,11,13,20,0,0,DateTimeKind.Utc);
        var quote = AlanchandDirhamRateSource.Parse(Table("تومان", "<b>۴۰٬۱۲۳</b>"), at);
        check(quote.TomanPerDirham == 40123 && quote.SourceUnit == "Toman", "automatic dirham selects labeled AED sell rate rather than buy rate");
        check(AlanchandDirhamRateSource.Parse(Table("ریال", "٤٠١٢٣٠"), at).TomanPerDirham == 40123,
            "automatic dirham explicitly converts rial to toman once and accepts Arabic digits");
        check(AlanchandDirhamRateSource.Parse(Table("تومان", "۴۰٬۱۲۳٫۵"), at).TomanPerDirham == 40123.5m,
            "automatic dirham retains decimal precision without rounding currency incorrectly");
        check(AlanchandDirhamRateSource.Parse(Table("تومان", "40123") + Table("تومان", "40123"), at).TomanPerDirham == 40123,
            "duplicate responsive copies must agree on the same rate");
        foreach (var invalid in new[] {
            Table("", "40123"), Table("تومان / ریال", "40123"), Table("تومان", "0"),
            Table("تومان", "-40123"), Table("تومان", "40,123 +5%"), Table("تومان", "ناموجود"),
            Table("تومان", "40123", "دلار آمریکا"), Table("تومان", "40123") + Table("تومان", "40500"),
            "<script>var AED=40123;</script><p>درهم امارات 40123 تومان</p>",
            "<html><h1>Checking your browser</h1></html>" })
        {
            var refused=false;
            try { AlanchandDirhamRateSource.Parse(invalid, at); } catch(InvalidOperationException) { refused=true; }
            check(refused, "automatic dirham refuses ambiguous, missing, non-AED or invalid price: " + invalid[..Math.Min(55,invalid.Length)]);
        }
        check(!AutomaticDirhamRateService.IsDue(at,at.AddHours(2).AddTicks(-1)) && AutomaticDirhamRateService.IsDue(at,at.AddHours(2)),
            "automatic dirham becomes due exactly two hours after the last successful observation");
        check(AutomaticDirhamRateService.IsDue(null,at) && !AutomaticDirhamRateService.IsDue(at.AddMinutes(1),at),
            "automatic dirham handles first startup and future clock skew without repeated refreshes");
        using(var http = new HttpClient(new StaticResponse("text/html",Table("تومان","40123"))))
            check((await new AlanchandDirhamRateSource(http).FetchAsync(default)).TomanPerDirham == 40123,
                "automatic dirham HTTP reader parses the fixed source document");
        foreach(var media in new[]{"application/json","text/plain"})
        {
            using var http = new HttpClient(new StaticResponse(media,Table("تومان","40123")));
            var refused=false;
            try { await new AlanchandDirhamRateSource(http).FetchAsync(default); } catch(InvalidOperationException) { refused=true; }
            check(refused,"automatic dirham rejects an unexpected HTTP media type: " + media);
        }

        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION")??throw new InvalidOperationException("Isolated SQL required");
        var selector=new SqlConnectionStringBuilder(connection);
        if(selector.InitialCatalog!="ZibasheERPCloudDev"||selector.DataSource!="127.0.0.1,14333")throw new InvalidOperationException("Wrong test database");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        // Future fixture timestamps isolate the worker cadence from existing development data.
        var now=DateTime.UtcNow.AddDays(1000);
        var clock=new TestClock(now);var service=new AutomaticDirhamRateService(db,clock);
        try
        {
            var before=AlanchandDirhamRateSource.Parse(Table("تومان","40123"),now);
            check(await service.RegisterAsync(before,default),"automatic dirham atomically appends a timestamped rate and source receipt");
            var stored=await db.DirhamExchangeRates.AsNoTracking().SingleAsync(r=>r.RecordedByUserId==0&&r.EffectiveAtUtc==now);
            var receipt=await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==stored.Id);
            check(stored.RecordedByUserId==0 && stored.EffectiveAtUtc==now && receipt.Payload.Contains("alanchand.com") && receipt.Status==NotificationOutboxStatus.Processed,
                "automatic rate retains source provenance without sending customer notifications");
            clock.Now=now.AddHours(1);
            check(!await service.RegisterAsync(before with{FetchedAtUtc=clock.Now},default),"restart or duplicate worker cannot append another rate inside two hours");
            clock.Now=now.AddHours(2);
            check(await service.RegisterAsync(before with{TomanPerDirham=41000,FetchedAtUtc=clock.Now},default),"next two-hour refresh may record a new market rate");
            var old=await db.DirhamExchangeRates.AsNoTracking().SingleAsync(r=>r.Id==stored.Id);
            check(old.TomanPerDirham==40123 && old.EffectiveAtUtc==now,"automatic refresh never rewrites an earlier historical rate");
            var refused=false;
            try { await service.RegisterAsync(before with{FetchedAtUtc=now.AddHours(-1)},default); } catch(InvalidOperationException) { refused=true; }
            check(refused,"expired observations never become today's exchange rate");
        }
        finally
        {
            var fixtureIds=await db.DirhamExchangeRates.Where(r=>r.RecordedByUserId==0&&(r.EffectiveAtUtc==now||r.EffectiveAtUtc==now.AddHours(2))).Select(r=>r.Id).ToArrayAsync();
            await db.NotificationOutbox.Where(n=>n.Channel==AutomaticDirhamRateService.ReceiptChannel&&fixtureIds.Contains(n.Id)).ExecuteDeleteAsync();
            await db.DirhamExchangeRates.Where(r=>fixtureIds.Contains(r.Id)).ExecuteDeleteAsync();
        }
    }

    private sealed class TestClock(DateTime now):TimeProvider
    {
        public DateTime Now {get;set;}=now;
        public override DateTimeOffset GetUtcNow()=>new(DateTime.SpecifyKind(Now,DateTimeKind.Utc));
    }
    private sealed class StaticResponse(string media,string body):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){RequestMessage=request,Content=new StringContent(body,System.Text.Encoding.UTF8,media)});
    }
}
