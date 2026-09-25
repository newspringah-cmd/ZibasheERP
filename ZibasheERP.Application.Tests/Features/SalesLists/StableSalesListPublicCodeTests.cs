using Xunit;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Tests.Features.SalesLists;

public sealed class StableSalesListPublicCodeTests
{
    [Fact]
    public void DisplayCode_WithoutStableCode_UsesUniqueCycleCode()
    {
        var list = new SalesList { PublicCode = 1307 };

        Assert.Equal(1307, list.DisplayCode);
    }

    [Fact]
    public void DisplayCode_ForRolledList_KeepsOriginalCustomerFacingCode()
    {
        var list = new SalesList
        {
            PublicCode = 5147,
            StablePublicCode = 1307
        };

        Assert.Equal(1307, list.DisplayCode);
    }
}
