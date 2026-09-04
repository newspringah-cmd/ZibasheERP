using ZibasheERP.Application.Features.Integrations.ImportTelegramSalesLists;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Integrations;

public sealed class TelegramSalesListImportParserTests
{
    [Fact]
    public void Parse_NewFormat_PreservesGroupedVolumesAndGift()
    {
        const string text = """
            کد: 14956
            1957 Eau de Parfum
            #Chanel
            #unisex
            L. 2019
            شنل ۱۹۵۷ او د پارفام
            75ml
            قیمت هر میل: 1173000
            حداقل میل درخواستی: 1 ميل
            باقيمانده: 39 ميل
            5 ml:
            @Sahand911
            2 ml:
            @Lilliiya
            @Sahand911 for @iviedya
            Next Bottle:
            @next_user 3ml
            """;

        var result = TelegramSalesListImportParser.Parse(text);

        Assert.Equal(14956, result.PublicCode);
        Assert.Equal(75, result.TotalVolumeMl);
        Assert.Equal(1173000m, result.PricePerMl);
        Assert.Equal(4, result.Requests.Count);
        var gift = result.Requests.Single(value => value.GiftRecipientTelegramUsername is not null);
        Assert.Equal("Sahand911", gift.TelegramUsername);
        Assert.Equal("iviedya", gift.GiftRecipientTelegramUsername);
        Assert.Equal(2, gift.VolumeMl);
        Assert.Equal(SalesListRequestKind.NextBottle, result.Requests.Last().Kind);
    }

    [Fact]
    public void Parse_OldFormat_RecognizesBottleOwnerAndPersianDigits()
    {
        const string text = """
            Le Parfum de Therese edp #Frederic_Malle
            #forwomen
            #formen
            L. 1950
            فردریک مال لو پارفام د ترز
            100ml
            قیمت هر میل 230/000
            حداقل میل درخواستی: ۱ میل
            Bottle: @promise_1995 25ml
            @Blueplanet222 2ml
            """;

        var result = TelegramSalesListImportParser.Parse(text);

        Assert.Equal(230000m, result.PricePerMl);
        Assert.Equal(1, result.MinimumRequestVolumeMl);
        Assert.Equal(2, result.Requests.Count);
        Assert.True(result.Requests.First().IsBottleOwner);
        Assert.Equal(25, result.Requests.First().VolumeMl);
        Assert.Equal(PerfumeGender.Unisex, result.Gender);
        Assert.True(result.Issues.Contains("missing_public_code"));
    }

    [Fact]
    public void Parse_StandaloneGiftRecipient_IsFlaggedForReview()
    {
        const string text = """
            Mon Parfum Pearl EDP
            L.2018
            میکالف مون پارفوم
            100ml
            قیمت هر میل: 95/000
            Bottle: @ImSwear 20ml
            for @ImSwear
            @Parisakimis 3ml
            """;

        var result = TelegramSalesListImportParser.Parse(text);

        Assert.True(result.Issues.Contains("ambiguous_standalone_gift_recipient"));
        Assert.False(result.IsSafeForAutomaticReview);
    }

    [Fact]
    public void Parse_NextBottleWithoutVolume_DefaultsToThirtyAndPreservesExplicitVolume()
    {
        const string text = """
            کد: 1234
            Sample Perfume
            #Sample_Brand
            L.2020
            عطر نمونه
            100ml
            قیمت هر میل: 100000
            حداقل میل درخواستی: 1 میل
            باقیمانده: 100 میل
            Next Bottle:
            @default_user
            50 ml:
            @explicit_user
            """;

        var result = TelegramSalesListImportParser.Parse(text);

        var next = result.Requests
            .Where(value => value.Kind == SalesListRequestKind.NextBottle)
            .ToArray();
        Assert.Equal(2, next.Length);
        Assert.Equal(30, next[0].VolumeMl);
        Assert.Equal(50, next[1].VolumeMl);
        Assert.False(result.Issues.Contains("request_without_volume"));
    }

    [Fact]
    public void Parse_LegacyExternalAndInstagramCustomers_PreservesNamesAndFancyBottle()
    {
        const string text = """
            کد: 7789
            Sample Perfume
            #Sample_Brand
            L.2020
            عطر نمونه
            75ml
            قیمت هر میل: 100000
            حداقل میل درخواستی: 1 میل
            باقیمانده: 67 میل
            5ml
            Razieh.108 insta F mokabi
            3ml:
            ali
            Next Bottle:
            .
            """;

        var result = TelegramSalesListImportParser.Parse(text);

        Assert.Equal(2, result.Requests.Count);
        Assert.Equal("Razieh.108 insta", result.Requests[0].TelegramUsername);
        Assert.True(result.Requests[0].IsExternalIdentity);
        Assert.True(result.Requests[0].IsFancyBottle);
        Assert.Equal("ali", result.Requests[1].TelegramUsername);
        Assert.False(result.Issues.Contains("request_without_volume"));
    }

    [Fact]
    public void Parse_LegacyExternalGift_PreservesExternalOwnerAndTelegramRecipient()
    {
        const string text = """
            کد: 12680
            Sample Perfume
            #Sample_Brand
            L.2020
            عطر نمونه
            100ml
            قیمت هر میل: 100000
            حداقل میل درخواستی: 1 میل
            باقیمانده: 97 میل
            3 ml:
            Haj mamad for @Arabidiet
            """;

        var result = TelegramSalesListImportParser.Parse(text);
        var gift = result.Requests.Single();

        Assert.Equal("Haj mamad", gift.TelegramUsername);
        Assert.True(gift.IsExternalIdentity);
        Assert.Equal("Arabidiet", gift.GiftRecipientTelegramUsername);
        Assert.False(gift.GiftRecipientIsExternalIdentity);
    }

    [Fact]
    public void Parse_CompleteEmptyList_IsSafeForReview()
    {
        const string text = """
            کد: 7948
            Baraonda
            #Nasomatto
            #unisex
            L.2016
            ناسوماتو برائوندا
            30ml
            قیمت هر میل: 1106000
            حداقل میل درخواستی: 1 میل
            باقیمانده: 30 میل
            Next Bottle:
            اولین نفر صف باشید
            """;

        var result = TelegramSalesListImportParser.Parse(text);

        Assert.Equal(0, result.Requests.Count);
        Assert.True(result.IsSafeForAutomaticReview);
    }
}
