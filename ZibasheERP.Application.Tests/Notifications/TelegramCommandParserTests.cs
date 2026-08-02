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

    [Fact]
    public void Parse_Lists_ReturnsLists()
    {
        Assert.Equal(TelegramCommand.Lists, TelegramCommandParser.Parse("/lists"));
    }

    [Fact]
    public void Parse_Addresses_ReturnsAddresses()
    {
        Assert.Equal(TelegramCommand.Addresses, TelegramCommandParser.Parse("/addresses"));
    }

    [Fact]
    public void Parse_HelpAndCancel_ReturnCommands()
    {
        Assert.Equal(TelegramCommand.Help, TelegramCommandParser.Parse("/help"));
        Assert.Equal(TelegramCommand.Cancel, TelegramCommandParser.Parse("/cancel@ZibasheBot"));
    }

    [Fact]
    public void Parse_Balance_ReturnsBalance()
    {
        Assert.Equal(TelegramCommand.Balance, TelegramCommandParser.Parse("/balance"));
    }

    [Fact]
    public void Parse_TrackWithOrderNumber_ReturnsTrack()
    {
        Assert.Equal(TelegramCommand.Track, TelegramCommandParser.Parse("/track ZS-1001"));
    }
}
