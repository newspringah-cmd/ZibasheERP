namespace ZibasheERP.API.AddressLabels;

public sealed class AddressLabelOptions
{
    public const string SectionName = "AddressLabel";

    public bool Enabled { get; set; }
    public string OpenAiApiKey { get; set; } = string.Empty;
    public string OpenAiModel { get; set; } = "gpt-5-mini";
}
