using System.Text.RegularExpressions;

namespace ZibasheERP.Application.Services;

public static class ManualInvoiceInputRules
{
    public static bool IsCustomerIdentityOnly(string description) => Regex.IsMatch(description.Trim(),
        @"^(?:@|https://t\.me/)[A-Za-z][A-Za-z0-9_]{4,31}/?$", RegexOptions.CultureInvariant);
}
