using ZibasheERP.Application.Notifications;
using Xunit;

namespace ZibasheERP.Application.Tests.Notifications;

public sealed class TelegramCallbackParserTests
{
    [Fact]
    public void Parse_MainMenuActions_ReturnExpectedTypes()
    {
        Assert.Equal(
            TelegramCallbackType.MenuLists,
            TelegramCallbackParser.Parse("menu:lists").Type);
        Assert.Equal(
            TelegramCallbackType.MenuOrders,
            TelegramCallbackParser.Parse("menu:orders").Type);
        Assert.Equal(
            TelegramCallbackType.MenuBalance,
            TelegramCallbackParser.Parse("menu:balance").Type);
        Assert.Equal(
            TelegramCallbackType.MenuAddresses,
            TelegramCallbackParser.Parse("menu:addresses").Type);
    }

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
    public void Parse_CancelDraft_RoundTripsCompactGuid()
    {
        var draftId = Guid.NewGuid();
        var result = TelegramCallbackParser.Parse(
            $"cancel:{TelegramCallbackParser.EncodeGuid(draftId)}");

        Assert.Equal(TelegramCallbackType.Cancel, result.Type);
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

    [Fact]
    public void Parse_TrackOrder_RoundTripsCompactGuid()
    {
        var orderId = Guid.NewGuid();
        var result = TelegramCallbackParser.Parse(
            $"track:{TelegramCallbackParser.EncodeGuid(orderId)}");

        Assert.Equal(TelegramCallbackType.TrackOrder, result.Type);
        Assert.Equal(orderId, result.SalesListId);
    }

    [Fact]
    public void Parse_ViewInvoice_RoundTripsCompactGuid()
    {
        var orderId = Guid.NewGuid();
        var result = TelegramCallbackParser.Parse(
            $"invoice:{TelegramCallbackParser.EncodeGuid(orderId)}");

        Assert.Equal(TelegramCallbackType.ViewInvoice, result.Type);
        Assert.Equal(orderId, result.SalesListId);
    }

    [Fact]
    public void Parse_SetDefaultAddress_RoundTripsCompactGuid()
    {
        var addressId = Guid.NewGuid();
        var result = TelegramCallbackParser.Parse(
            $"defaultaddr:{TelegramCallbackParser.EncodeGuid(addressId)}");

        Assert.Equal(TelegramCallbackType.SetDefaultAddress, result.Type);
        Assert.Equal(addressId, result.SalesListId);
    }

    [Fact]
    public void Parse_DeleteAddressActions_RoundTripCompactGuid()
    {
        var addressId = Guid.NewGuid();

        Assert.Equal(
            TelegramCallbackType.RequestDeleteAddress,
            TelegramCallbackParser.Parse(
                $"deleteaddr:{TelegramCallbackParser.EncodeGuid(addressId)}").Type);
        var confirmation = TelegramCallbackParser.Parse(
            $"confirmdeleteaddr:{TelegramCallbackParser.EncodeGuid(addressId)}");
        Assert.Equal(TelegramCallbackType.ConfirmDeleteAddress, confirmation.Type);
        Assert.Equal(addressId, confirmation.SalesListId);
    }

    [Fact]
    public void Parse_SetDeliveryAddress_RoundTripsBothIds()
    {
        var orderId = Guid.NewGuid();
        var addressId = Guid.NewGuid();
        var result = TelegramCallbackParser.Parse(
            $"setaddr:{TelegramCallbackParser.EncodeGuid(orderId)}:{TelegramCallbackParser.EncodeGuid(addressId)}");

        Assert.Equal(TelegramCallbackType.SetDeliveryAddress, result.Type);
        Assert.Equal(orderId, result.SalesListId);
        Assert.Equal(addressId, result.BottleId);
    }
}
