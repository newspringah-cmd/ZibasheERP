using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Services;

public sealed record SalesListProductionRow(SalesListRequest Request, int VolumeMl, int SourceCount = 1);

/// <summary>Combines physical gift portions without changing reservations or billing.</summary>
public static class SalesListProductionGrouping
{
    public static IReadOnlyList<SalesListProductionRow> Build(IEnumerable<SalesListRequest> source)
    {
        var requests = source.Where(request => !request.IsDeleted &&
            request.Kind == SalesListRequestKind.CurrentBottle &&
            request.Status is SalesListRequestStatus.Confirmed or SalesListRequestStatus.Promoted or
                SalesListRequestStatus.QueuedForInvoice or SalesListRequestStatus.Invoiced).ToArray();
        var owners = requests.Where(request => request.IsBottleOwner)
            .GroupBy(request => request.SalesListId).ToDictionary(group => group.Key, group => group.First());
        var aliases = requests.SelectMany(request => new[]
            {
                (request.SalesListId, Username: Normalize(request.TelegramUsername), Id: request.TelegramUserId.Trim()),
                (request.SalesListId, Username: Normalize(request.GiftRecipientTelegramUsername), Id: request.GiftRecipientTelegramUserId?.Trim())
            })
            .Where(identity => identity.Username.Length > 0 && !string.IsNullOrWhiteSpace(identity.Id))
            .GroupBy(identity => (identity.SalesListId, identity.Username))
            .ToDictionary(group => group.Key, group => group.Select(identity => identity.Id!).Distinct().ToArray());

        string RecipientKey(SalesListRequest request, int index)
        {
            if (!string.IsNullOrWhiteSpace(request.GiftRecipientTelegramUserId))
                return "id:" + request.GiftRecipientTelegramUserId.Trim();
            var username = Normalize(request.GiftRecipientTelegramUsername);
            if (username.Length == 0) return "unknown:" + index;
            return aliases.TryGetValue((request.SalesListId, username), out var ids) && ids.Length == 1
                ? "id:" + ids[0] : "username:" + username;
        }

        bool OwnerGift(SalesListRequest request) => request.IsGift && !request.IsBottleOwner &&
            owners.TryGetValue(request.SalesListId, out var owner) && IsGiftFor(request, owner);

        var rows = requests.Where(request => !request.IsGift || request.IsBottleOwner)
            .Select(request => new SalesListProductionRow(request, request.VolumeMl +
                (request.IsBottleOwner ? requests.Where(gift => gift.SalesListId == request.SalesListId &&
                    OwnerGift(gift)).Sum(gift => gift.VolumeMl) : 0)))
            .ToList();
        var gifts = requests.Select((request, index) => (Request: request, Index: index))
            .Where(item => item.Request.IsGift && !item.Request.IsBottleOwner && !OwnerGift(item.Request))
            .GroupBy(item => (
                item.Request.SalesListId, Recipient: RecipientKey(item.Request, item.Index),
                Type: item.Request.Bottle?.Type ?? BottleType.Normal,
                FancyName: item.Request.Bottle?.Type == BottleType.Fancy ? Normalize(item.Request.Bottle.Name) : "",
                Label: item.Request.LabelIdentityText?.Trim() ?? "", item.Request.OmitIdentityOnLabel));
        foreach (var group in gifts)
        {
            var ordered = group.Select(item => item.Request)
                .OrderBy(request => request.ConfirmedAt ?? request.CreatedAt).ThenBy(request => request.Id).ToArray();
            rows.Add(new(ordered[0], ordered.Sum(request => request.VolumeMl), ordered.Length));
        }
        return rows;
    }

    private static bool IsGiftFor(SalesListRequest gift, SalesListRequest recipient) =>
        (!string.IsNullOrWhiteSpace(gift.GiftRecipientTelegramUserId) &&
         string.Equals(gift.GiftRecipientTelegramUserId.Trim(), recipient.TelegramUserId.Trim(), StringComparison.Ordinal)) ||
        (Normalize(gift.GiftRecipientTelegramUsername).Length > 0 &&
         Normalize(gift.GiftRecipientTelegramUsername) == Normalize(recipient.TelegramUsername));

    private static string Normalize(string? value) => value?.Trim().TrimStart('@').ToLowerInvariant() ?? "";
}
