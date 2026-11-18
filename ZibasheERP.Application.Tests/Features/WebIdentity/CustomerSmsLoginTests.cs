using Xunit;
using ZibasheERP.Application.WebIdentity;

namespace ZibasheERP.Application.Tests.Features.WebIdentity;

public sealed class CustomerSmsLoginTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-18T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Directory : IVerifiedSmsCustomerDirectory
    {
        public List<SmsLoginCustomer> Customers { get; } = [new(Guid.NewGuid(), "09120000000", true)];
        public Task<IReadOnlyList<SmsLoginCustomer>> FindByVerifiedMobileAsync(string mobile, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SmsLoginCustomer>>(Customers.Where(c => c.VerifiedMobile == mobile).ToArray());
        public Task<SmsLoginCustomer?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Customers.FirstOrDefault(c => c.CustomerId == id));
    }
    private sealed class Sender : ICustomerLoginSmsSender
    {
        public List<string> Codes { get; } = [];
        public bool Fail { get; set; }
        public Task SendLoginCodeAsync(string mobile, string code, CancellationToken ct)
        {
            if (Fail) throw new InvalidOperationException("synthetic-provider-error");
            Codes.Add(code);
            return Task.CompletedTask;
        }
    }
    private sealed class Scenario : IDisposable
    {
        public Clock Clock { get; } = new();
        public Directory Directory { get; } = new();
        public Sender Sender { get; } = new();
        public StagingCustomerSmsLogin Service { get; }
        public Scenario() => Service = new(Directory, Sender, Clock);
        public Task<SmsLoginStart> Start(string phone = "09120000000", string client = "test-client") => Service.BeginAsync(phone, client);
        public void Dispose() => Service.Dispose();
    }

    [Fact]
    public void Normalize_AcceptsPersianArabicAndInternationalPrefix_RejectsInvalidPhones()
    {
        foreach (var value in new[] { "۰۹۱۲۰۰۰۰۰۰۰", "٠٩١٢٠٠٠٠٠٠٠", "+98 912-000-0000", "00989120000000", "09120000000" })
            Assert.Equal("09120000000", IranianLoginMobile.Normalize(value));
        foreach (var value in new[] { "", "username", "02112345678", "+449120000000", "09120000000<script>", "0912+0000000" })
            Assert.Null(IranianLoginMobile.Normalize(value));
    }
    [Fact]
    public async Task Verify_ValidCodeReturnsStableCustomerOnly_AndCannotReplay()
    {
        using var s = new Scenario();
        var start = await s.Start();
        Assert.Equal(SmsLoginStartStatus.Accepted, start.Status);
        var verified = await s.Service.VerifyAsync(start.ChallengeId, s.Sender.Codes.Single(), "test-client");
        Assert.Equal(s.Directory.Customers[0].CustomerId, verified!.CustomerId);
        Assert.Null(await s.Service.VerifyAsync(start.ChallengeId, s.Sender.Codes.Single(), "test-client"));
    }
    [Fact]
    public async Task Verify_ConcurrentSubmissionsSucceedExactlyOnce()
    {
        using var s = new Scenario(); var start = await s.Start(); var code = s.Sender.Codes.Single();
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => s.Service.VerifyAsync(start.ChallengeId, code, "test-client")));
        Assert.Equal(1, results.Count(r => r is not null));
    }
    [Fact]
    public async Task Verify_ExpiresAtExactlyThreeMinutes()
    {
        using var s = new Scenario(); var start = await s.Start();
        s.Clock.Now += TimeSpan.FromMinutes(3);
        Assert.Null(await s.Service.VerifyAsync(start.ChallengeId, s.Sender.Codes.Single(), "test-client"));
    }
    [Fact]
    public async Task Verify_LocksAfterFiveWrongAttemptsIncludingMalformedCodes()
    {
        using var s = new Scenario(); var start = await s.Start();
        for (var i = 0; i < 5; i++) Assert.Null(await s.Service.VerifyAsync(start.ChallengeId, "wrong", "test-client"));
        Assert.Null(await s.Service.VerifyAsync(start.ChallengeId, s.Sender.Codes.Single(), "test-client"));
    }
    [Fact]
    public async Task Begin_CooldownAndResendInvalidatePreviousChallenge()
    {
        using var s = new Scenario(); var first = await s.Start();
        Assert.Equal(SmsLoginStartStatus.Throttled, (await s.Start()).Status);
        s.Clock.Now += TimeSpan.FromSeconds(60); var second = await s.Start();
        Assert.Null(await s.Service.VerifyAsync(first.ChallengeId, s.Sender.Codes[0], "test-client"));
        Assert.NotNull(await s.Service.VerifyAsync(second.ChallengeId, s.Sender.Codes[1], "test-client"));
    }
    [Fact]
    public async Task Begin_LimitsEachMobileAcrossDifferentClients()
    {
        using var s = new Scenario();
        for (var i = 0; i < 3; i++) { Assert.Equal(SmsLoginStartStatus.Accepted, (await s.Start(client: "client-" + i)).Status); s.Clock.Now += TimeSpan.FromSeconds(60); }
        Assert.Equal(SmsLoginStartStatus.Throttled, (await s.Start(client: "another-client")).Status);
        s.Clock.Now += TimeSpan.FromMinutes(15);
        Assert.Equal(SmsLoginStartStatus.Accepted, (await s.Start()).Status);
    }
    [Fact]
    public async Task Begin_LimitsClientAcrossDifferentMobiles()
    {
        using var s = new Scenario();
        for (var i = 0; i < 10; i++) Assert.Equal(SmsLoginStartStatus.Accepted, (await s.Start("091200000" + i.ToString("D2"))).Status);
        Assert.Equal(SmsLoginStartStatus.Throttled, (await s.Start("09121111111")).Status);
    }
    [Fact]
    public async Task Begin_UnknownAmbiguousAndInactiveLookAcceptedButNeverSendOrAuthenticate()
    {
        foreach (var kind in new[] { "unknown", "ambiguous", "inactive" })
        {
            using var s = new Scenario();
            if (kind == "unknown") s.Directory.Customers.Clear();
            if (kind == "ambiguous") s.Directory.Customers.Add(new(Guid.NewGuid(), "09120000000", true));
            if (kind == "inactive") s.Directory.Customers[0] = s.Directory.Customers[0] with { IsActive = false };
            var start = await s.Start();
            Assert.Equal(SmsLoginStartStatus.Accepted, start.Status);
            Assert.Equal(0, s.Sender.Codes.Count);
            Assert.Null(await s.Service.VerifyAsync(start.ChallengeId, "000000", "test-client"));
        }
    }
    [Fact]
    public async Task Begin_ProviderFailureDoesNotEscapeAndCannotAuthenticate()
    {
        using var s = new Scenario(); s.Sender.Fail = true;
        var start = await s.Start(); Assert.Equal(SmsLoginStartStatus.Accepted, start.Status);
        Assert.Null(await s.Service.VerifyAsync(start.ChallengeId, "000000", "test-client"));
    }
    [Fact]
    public async Task Verify_RechecksCurrentBindingAndBlockedCustomer()
    {
        foreach (var kind in new[] { "inactive", "rebound", "deleted" })
        {
            using var s = new Scenario(); var start = await s.Start();
            if (kind == "inactive") s.Directory.Customers[0] = s.Directory.Customers[0] with { IsActive = false };
            if (kind == "rebound") s.Directory.Customers[0] = s.Directory.Customers[0] with { VerifiedMobile = "09121111111" };
            if (kind == "deleted") s.Directory.Customers.Clear();
            Assert.Null(await s.Service.VerifyAsync(start.ChallengeId, s.Sender.Codes.Single(), "test-client"));
        }
    }
    [Fact]
    public async Task Verify_AcceptsPersianDigitsAndThrottlesClientEvenAcrossUnknownChallenges()
    {
        using var s = new Scenario(); var start = await s.Start();
        var persian = new string(s.Sender.Codes.Single().Select(c => (char)('۰' + c - '0')).ToArray());
        Assert.NotNull(await s.Service.VerifyAsync(start.ChallengeId, persian, "test-client"));
        s.Clock.Now += TimeSpan.FromSeconds(60); var next = await s.Start();
        for (var i = 0; i < 30; i++) await s.Service.VerifyAsync(new string('A', 64), "123456", "another-client");
        Assert.Null(await s.Service.VerifyAsync(next.ChallengeId, s.Sender.Codes.Last(), "another-client"));
    }
}
