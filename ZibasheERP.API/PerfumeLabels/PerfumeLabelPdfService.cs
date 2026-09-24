using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ZibasheERP.API.PerfumeLabels;

public sealed record PerfumeLabelEntry(int? VolumeMl);

public interface IPerfumeLabelPdfService
{
    byte[] Create(byte[] logo, IReadOnlyCollection<PerfumeLabelEntry> entries);
}

public sealed class PerfumeLabelPdfService : IPerfumeLabelPdfService
{
    public PerfumeLabelPdfService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public byte[] Create(byte[] logo, IReadOnlyCollection<PerfumeLabelEntry> entries)
    {
        if (logo.Length == 0)
            throw new InvalidOperationException("فایل لوگو خالی است.");
        if (entries.Count == 0)
            throw new InvalidOperationException("آیتمی برای چاپ لوگو وجود ندارد.");

        var pages = entries.Chunk(6).ToArray();
        var document = Document.Create(container =>
        {
            foreach (var pageEntries in pages)
            {
                container.Page(page =>
                {
                    // Physical stock: 80 mm width × 60 mm height.
                    // Six equal 26.67 × 30 mm labels: three columns and two rows.
                    page.Size(new PageSize(80, 60, Unit.Millimetre));
                    page.Margin(0);
                    page.Content().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.RelativeColumn();
                            columns.RelativeColumn();
                            columns.RelativeColumn();
                        });

                        for (var index = 0; index < 6; index++)
                        {
                            var entry = index < pageEntries.Length ? pageEntries[index] : null;
                            table.Cell().Height(29.8f, Unit.Millimetre)
                                .Border(0.35f).BorderColor(Colors.Grey.Medium)
                                .Padding(1.2f, Unit.Millimetre)
                                .Element(cell => ComposeCell(cell, logo, entry));
                        }
                    });
                });
            }
        });
        return document.GeneratePdf();
    }

    private static void ComposeCell(IContainer container, byte[] logo, PerfumeLabelEntry? entry)
    {
        container.Column(column =>
        {
            column.Spacing(1);
            column.Item().Height(18, Unit.Millimetre).AlignCenter().AlignMiddle()
                .Image(logo).FitArea();
            column.Item().Height(6, Unit.Millimetre).AlignCenter().AlignMiddle()
                .Text(entry?.VolumeMl.HasValue == true ? $"{entry.VolumeMl.Value}ml" : string.Empty)
                .FontFamily(Fonts.Arial).FontSize(13).SemiBold();
        });
    }
}
