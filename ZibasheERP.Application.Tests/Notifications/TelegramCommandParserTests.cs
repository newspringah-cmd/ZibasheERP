using ZibasheERP.Application.Notifications;
using Xunit;

namespace ZibasheERP.Application.Tests.Notifications;

public sealed class TelegramCommandParserTests
{
    [Fact]
    public void Parse_StartWithPayload_ReturnsStart()
    {
        Assert.Equal(TelegramCommand.Start, TelegramCommandParser.Parse("/start campaign"));
    }

    [Fact]
    public void Parse_CommandWithBotName_ReturnsOrders()
    {
        Assert.Equal(TelegramCommand.Orders, TelegramCommandParser.Parse("/orders@ZibasheBot"));
    }

    [Fact]
    public void Parse_UnsupportedText_ReturnsUnknown()
    {
        Assert.Equal(TelegramCommand.Unknown, TelegramCommandParser.Parse("سلام"));
    }
}
