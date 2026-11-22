using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZibasheERP.API.AddressLabels;
using ZibasheERP.Domain.Entities;

public static class AddressLabelPrintTests
{
    public static async Task Run(Action<bool, string> check, string? renderDirectory = null)
    {
        const BindingFlags helpers = BindingFlags.Static | BindingFlags.NonPublic;
        var phoneMethod = typeof(AddressLabelService).GetMethod("NormalizePrintPhones", helpers)!;
        var restoreMethod = typeof(AddressLabelService).GetMethod("RestoreInlineDestinations", helpers)!;
        string Phone(string value) => (string)phoneMethod.Invoke(null, [value])!;
        string Restore(string address, string city, string original) =>
            (string)restoreMethod.Invoke(null, [address, city, original])!;

        check(Phone("۰۹۱۲ ۱۲۳–۴۵۶۷، (۰۲۴) ٣٣٣٣-٤٤٤٤ / +۹۸ ۹۳۵-۱۱۱ ۲۲۲۲") ==
              "09121234567، 02433334444، +989351112222",
            "address label prints each mobile and landline without internal separators");
        check(Phone("0912 123-4567 0913 123 4567") == "09121234567، 09131234567",
            "two complete phones separated only by whitespace remain distinct");
        check(Phone("۰۰۹۸ ۹۱۲ ۱۲۳ ۴۵۶۷، ٣٣٣٣-٤٤٤٤") == "00989121234567، 33334444",
            "label keeps international prefixes and locally written landlines");

        const string source = "شهر: زنجان\nخیابان نمونه، اداره پست زنجان پلاک ۷\nنام گیرنده: علی نمونه";
        const string addressText = "خیابان نمونه، اداره پست زنجان پلاک ۷";
        check(Restore("خیابان نمونه، اداره پست پلاک ۷", "زنجان", source) == addressText,
            "label restores a city omitted from the middle of an institution name");
        check(Restore("اداره برق، پلاک ۱۰", "آذربایجان شرقی", "آذربایجان شرقی، اداره برق آذربايجان شرقي، پلاک ۱۰") ==
              "اداره برق آذربايجان شرقي، پلاک ۱۰", "label restores a multiword province inside a place name, including Arabic letter variants");
        check(Restore(addressText, "زنجان", source) == addressText &&
              Restore("خیابان نمونه پلاک ۷", "زنجان", "شهر: زنجان\nخیابان نمونه پلاک ۷") == "خیابان نمونه پلاک ۷",
            "label neither duplicates a name already in the address nor inserts a standalone city");
        check(Restore("اداره پست حجتیه", "زنجان", "زنجان، اداره پست زنجانسر") == "اداره پست حجتیه",
            "a city does not match the beginning of a different place name");

        if (renderDirectory is null) return;
        var environment = new LabelEnvironment();
        using var service = new AddressLabelService(Options.Create(new AddressLabelOptions
        {
            Enabled = true, OpenAiApiKey = "synthetic"
        }), environment, NullLogger<AddressLabelService>.Instance);
        var clientField = typeof(AddressLabelService).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = (HttpClient)clientField.GetValue(service)!;
        original.Dispose();
        using var client = new HttpClient(new LabelResponseHandler()) { BaseAddress = new Uri("https://example.invalid/") };
        clientField.SetValue(service, client);
        var structured = new Address
        {
            ReceiverName = "علی نمونه", FullAddress = addressText, Mobile = "۰۹۱۲ ۱۲۳-۴۵۶۷",
            PostalCode = "۰۱۲۳-۴۵۶۷۸۹", City = "زنجان"
        };
        var raw = new Address
        {
            FullAddress = source + "\nتماس: ۰۹۱۲ ۱۲۳-۴۵۶۷، ۰۲۴ ۳۳۳۳-۴۴۴۴",
            Description = "آدرس خام ثبت‌شده توسط حسابدار"
        };
        Directory.CreateDirectory(renderDirectory);
        foreach (var (name, input) in new[] { ("structured", structured), ("raw", raw) })
        {
            var label = await service.CreateAsync(input, CancellationToken.None);
            check(label.IsSuccessful && label.Pdf is { Length: > 0 } && label.Preview is { Length: > 0 },
                $"{name} address produces the real label PDF and JPEG");
            await File.WriteAllBytesAsync(Path.Combine(renderDirectory, name + ".pdf"), label.Pdf!);
            await File.WriteAllBytesAsync(Path.Combine(renderDirectory, name + ".jpg"), label.Preview!);
        }
    }

    private sealed class LabelEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "label-test";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class LabelResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var parsed = new ParsedPostalAddress("علی نمونه", "خیابان نمونه، اداره پست پلاک ۷",
                "۰۹۱۲ ۱۲۳-۴۵۶۷، ۰۲۴ ۳۳۳۳-۴۴۴۴", "۰۱۲۳-۴۵۶۷۸۹", "زنجان", "");
            var body = JsonSerializer.Serialize(new
            {
                output = new[] { new { content = new[] { new { type = "output_text", text = JsonSerializer.Serialize(parsed) } } } }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
