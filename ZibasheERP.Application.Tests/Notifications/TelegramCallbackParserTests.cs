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
}
