using ZibasheERP.Application.Notifications;
using Xunit;

namespace ZibasheERP.Application.Tests.Notifications;

public sealed class TelegramPaymentCommandParserTests
{
    [Fact]
    public void Parse_ValidPaymentCommand_ReturnsValues()
    {
        var result = TelegramPaymentCommandParser.Parse("/pay ZS-1001 TX-42");

        Assert.NotNull(result);
        Assert.Equal("ZS-1001", result!.OrderNumber);
        Assert.Equal("TX-42", result.TransactionId);
    }

    [Fact]
    public void Parse_IncompleteCommand_ReturnsNull()
    {
        Assert.Null(TelegramPaymentCommandParser.Parse("/pay ZS-1001"));
    }
}
