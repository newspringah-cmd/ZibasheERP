using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ZibasheERP.API.Inventory;

public static class InventoryPromotionPhotoPage
{
    public static byte[] Render(IReadOnlyList<int> codes, IReadOnlyList<byte[]?> photos)
    {
        if (codes.Count is <1 or >50 || codes.Count != photos.Count)
            throw new ArgumentException("A photo page requires one to fifty inventory codes and matching photo slots.");
        QuestPDF.Settings.License = LicenseType.Community;
        const int columns = 5;
        const int cellWidth = 220;
        const int cellHeight = 180;
        var rows = (codes.Count + columns - 1) / columns;
        var images = photos.Select(photo =>
        {
            if (photo is null) return null;
            try { return Image.FromBinaryData(photo); }
            catch (Exception) { return null; }
        }).ToArray();
        try
        {
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
                                var picture = tile.Item().Height(130).AlignMiddle().AlignCenter();
                                if (image is not null) picture.Image(image).FitArea();
                                else picture.Text("—").FontSize(24);
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
