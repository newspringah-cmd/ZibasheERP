using QuestPDF.Fluent;
using QuestPDF.Drawing;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using ZibasheERP.API.Marketing;

namespace ZibasheERP.API.Inventory;

public static class InventoryPromotionPhotoPage
{
    public static byte[] Render(IReadOnlyList<int> codes, IReadOnlyList<byte[]?> photos)
        => Render(codes, photos, null);

    private const string TitleFont = "Zibashe Inventory Titles";
    private static readonly Lazy<bool> RegisteredTitleFont = new(() =>
    {
        using var font = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "B-NAZANIN.TTF"));
        FontManager.RegisterFontWithCustomName(TitleFont, font);
        return true;
    });

    public static byte[] RenderAdvertisement(IReadOnlyList<InventoryPromotionImage> items, IReadOnlyList<byte[]?> photos)
    {
        if (items.Count > InventoryPromotionBatch.MaxAnnouncementPhotos || photos.Any(photo => photo is null))
            throw new ArgumentException("Every advertised perfume needs a photo.");
        _ = RegisteredTitleFont.Value;
        return Render(items.Select(item => item.Code).ToArray(), photos, items.Select(item => item.Name).ToArray());
    }

    private static byte[] Render(IReadOnlyList<int> codes, IReadOnlyList<byte[]?> photos, string[]? titles)
    {
        if (codes.Count is <1 or >50 || codes.Count != photos.Count)
            throw new ArgumentException("A photo page requires one to fifty inventory codes and matching photo slots.");
        QuestPDF.Settings.License = LicenseType.Community;
        var columns = titles is null ? 5 : Math.Min(3, codes.Count);
        var cellWidth = titles is null ? 220 : 280;
        var cellHeight = titles is null ? 180 : 265;
        var rows = (codes.Count + columns - 1) / columns;
        var images = photos.Select(photo =>
        {
            if (photo is null) return null;
            try { return Image.FromBinaryData(photo); }
            catch (Exception) { return null; }
        }).ToArray();
        try
        {
            if (titles is not null && images.Any(image => image is null))
                throw new ArgumentException("An advertised perfume photo could not be decoded.");
            return Document.Create(document => document.Page(page =>
            {
                page.Size(cellWidth * columns, cellHeight * rows);
                page.PageColor(Colors.White);
                page.Content().Table(table =>
                {
                    table.ColumnsDefinition(definition =>
                    {
                        for (var index = 0; index < columns; index++) definition.RelativeColumn();
                    });
                    for (var index = 0; index < codes.Count; index++)
                    {
                        var image = images[index];
                        var label = $"{index + 1}  |  #{codes[index]}";
                        table.Cell().Height(cellHeight).Padding(4).Border(1).BorderColor(Colors.Grey.Lighten2)
                            .Padding(5).Column(tile =>
                            {
                                if (titles is not null)
                                {
                                    tile.Item().Height(48).PaddingHorizontal(5).ContentFromRightToLeft().AlignCenter().AlignMiddle()
                                        .ScaleToFit().Text($"{titles[index]} · {HourlyPromotionCopy.Number(codes[index])}")
                                        .FontFamily(TitleFont).FontSize(19).Bold();
                                }
                                var picture = tile.Item().Height(titles is null ? 130 : 195).AlignMiddle().AlignCenter();
                                if (image is not null) picture.Image(image).FitArea();
                                else picture.Text("—").FontSize(24);
                                if (titles is null)
                                    tile.Item().PaddingTop(6).AlignCenter().Text(label).FontSize(18).Bold();
                            });
                    }
                });
            })).GenerateImages(new ImageGenerationSettings { ImageFormat = ImageFormat.Jpeg, RasterDpi = 144 }).Single();
        }
        finally
        {
            foreach (var image in images) image?.Dispose();
        }
    }
}
