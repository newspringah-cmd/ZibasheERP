using ZibasheERP.Application.Notifications;
using Xunit;

namespace ZibasheERP.Application.Tests.Notifications;

public sealed class TelegramAddressCommandParserTests
{
    [Fact]
    public void Parse_ValidCommand_ReturnsAllFields()
    {
        var result = TelegramAddressCommandParser.Parse(
            "/addaddress منزل | علی رضایی | تهران | تهران | ۱۲۳۴۵۶۷۸۹۰ | خیابان تست");

        Assert.NotNull(result);
        Assert.Equal("منزل", result!.Description);
        Assert.Equal("۱۲۳۴۵۶۷۸۹۰", result.PostalCode);
        Assert.Equal("خیابان تست", result.FullAddress);
    }

    [Fact]
    public void Parse_MissingField_ReturnsNull()
    {
        Assert.Null(TelegramAddressCommandParser.Parse(
            "/addaddress منزل | علی | تهران"));
    }
}
