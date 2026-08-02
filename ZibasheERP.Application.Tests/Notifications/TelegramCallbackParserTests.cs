using ZibasheERP.Application.Notifications;
using Xunit;

namespace ZibasheERP.Application.Tests.Notifications;

public sealed class TelegramCallbackParserTests
{
    [Fact]
    public void Parse_ListSelection_ReturnsSalesListId()
    {
        var id = Guid.NewGuid();
        var result = TelegramCallbackParser.Parse($"list:{id:N}");

        Assert.Equal(TelegramCallbackType.SelectSalesList, result.Type);
        Assert.Equal(id, result.SalesListId);
    }

    [Fact]
    public void Parse_VolumeSelection_ReturnsVolume()
    {
        var id = Guid.NewGuid();
        var result = TelegramCallbackParser.Parse($"volume:{id:N}:10");

        Assert.Equal(TelegramCallbackType.SelectVolume, result.Type);
        Assert.Equal(10, result.VolumeMl);
    }

    [Fact]
    public void Parse_InvalidData_ReturnsUnknown()
    {
        var result = TelegramCallbackParser.Parse("volume:invalid:10");

        Assert.Equal(TelegramCallbackType.Unknown, result.Type);
    }

    [Fact]
    public void Parse_ConfirmDraft_RoundTripsCompactGuid()
    {
        var draftId = Guid.NewGuid();
        var token = TelegramCallbackParser.EncodeGuid(draftId);
        var result = TelegramCallbackParser.Parse($"confirm:{token}");

        Assert.Equal(TelegramCallbackType.ConfirmOrder, result.Type);
        Assert.Equal(draftId, result.SalesListId);
    }

    [Fact]
    public void Parse_OrderDetails_RoundTripsCompactGuid()
    {
        var orderId = Guid.NewGuid();
        var result = TelegramCallbackParser.Parse(
            $"order:{TelegramCallbackParser.EncodeGuid(orderId)}");

        Assert.Equal(TelegramCallbackType.ViewOrder, result.Type);
        Assert.Equal(orderId, result.SalesListId);
    }

    [Fact]
    public void Parse_StartPayment_RoundTripsCompactGuid()
    {
        var orderId = Guid.NewGuid();
        var result = TelegramCallbackParser.Parse(
            $"pay:{TelegramCallbackParser.EncodeGuid(orderId)}");

        Assert.Equal(TelegramCallbackType.StartPayment, result.Type);
        Assert.Equal(orderId, result.SalesListId);
    }
}
