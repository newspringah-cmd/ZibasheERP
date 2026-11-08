using System.Globalization;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Services;

public sealed record SalesListProductionRow(SalesListRequest Request, decimal VolumeMl, int SourceCount = 1,
    string? PreparationNotes = null);

/// <summary>Physical recipient totals only; source reservations and billing stay separate.</summary>
public static class SalesListProductionGrouping
{
    private sealed record Identity(string NumericId,string Username);

    public static IReadOnlyList<SalesListProductionRow> Build(IEnumerable<SalesListRequest> source)
    {
        var requests=source.Where(r=>!r.IsDeleted&&r.Kind==SalesListRequestKind.CurrentBottle&&
            r.Status is SalesListRequestStatus.Confirmed or SalesListRequestStatus.Promoted or
                SalesListRequestStatus.QueuedForInvoice or SalesListRequestStatus.Invoiced).ToArray();
        Identity User(SalesListRequest r)=>Read(r.TelegramUserId,r.TelegramUsername);
        Identity Recipient(SalesListRequest r)=>r.IsGift&&!r.IsBottleOwner?Read(r.GiftRecipientTelegramUserId,r.GiftRecipientTelegramUsername):User(r);
        var aliases=requests.SelectMany(r=>new[]{(r.SalesListId,Identity:User(r)),(r.SalesListId,Identity:Recipient(r))})
            .Where(x=>x.Identity.NumericId.Length>0&&x.Identity.Username.Length>0)
            .GroupBy(x=>(x.SalesListId,x.Identity.Username))
            .ToDictionary(g=>g.Key,g=>g.Select(x=>x.Identity.NumericId).Distinct().ToArray());
        string Key(SalesListRequest r,int index)
        {
            var identity=Recipient(r);
            if(identity.NumericId.Length>0)return "id:"+identity.NumericId;
            if(identity.Username.Length>0)
            {
                if(aliases.TryGetValue((r.SalesListId,identity.Username),out var ids))
                    return ids.Length==1?"id:"+ids[0]:"ambiguous:"+index;
                return "username:"+identity.Username;
            }
            return "unknown:"+index;
        }
        string? Notes(SalesListRequest[] portions,SalesListRequest representative)
        {
            var notes=new List<string>();
            var conflict=false;
            var id=Recipient(representative);
            if(id.Username.Length>0&&aliases.TryGetValue((representative.SalesListId,id.Username),out var ids)&&ids.Length>1)
            {notes.Add("مشخصات گیرنده نیازمند بررسی است");conflict=true;}
            var packaging=portions.Select(r=>r.Bottle?.Type==BottleType.Fancy?"فانتزی "+r.Bottle.Name:"معمولی").Distinct().ToArray();
            if(packaging.Length>1){notes.Add("شیشه‌های ثبت‌شده: "+string.Join(" / ",packaging));conflict=true;}
            var labels=portions.Select(r=>r.OmitIdentityOnLabel?"بدون آیدی":r.LabelIdentityText?.Trim()??"").Distinct().ToArray();
            if(labels.Length>1){notes.Add("توضیح لیبل: "+string.Join(" / ",labels.Select(s=>s.Length==0?"پیش‌فرض":s)));conflict=true;}
            var comments=portions.Select(r=>r.AdminNotes?.Trim()).Where(s=>!string.IsNullOrWhiteSpace(s)).Distinct().ToArray();
            if(portions.Length>1&&comments.Length>0)notes.Add("توضیحات: "+string.Join(" / ",comments));
            if(notes.Count==0)return null;
            var text=(conflict?"⚠️ ":"📝 ")+string.Join("؛ ",notes);
            return text.Length>380?text[..377]+"…":text;
        }
        var rows=new List<SalesListProductionRow>();
        foreach(var group in requests.Select((r,i)=>(Request:r,Index:i)).GroupBy(x=>(x.Request.SalesListId,Recipient:Key(x.Request,x.Index))))
        {
            var portions=group.Select(x=>x.Request).OrderBy(r=>r.ConfirmedAt??r.CreatedAt).ThenBy(r=>r.Id).ToArray();
            var gifts=portions.Where(r=>r.IsGift&&!r.IsBottleOwner).ToArray();
            var own=portions.Where(r=>!r.IsGift||r.IsBottleOwner).ToArray();
            if(gifts.Length==0)
            {
                rows.AddRange(own.Select(r=>new SalesListProductionRow(r,r.VolumeMl,PreparationNotes:Notes([r],r))));continue;
            }
            var owner=own.FirstOrDefault(r=>r.IsBottleOwner);
            if(owner!=null)
            {
                rows.Add(new(owner,portions.Sum(r=>r.VolumeMl),portions.Length,Notes(portions,owner)));continue;
            }
            // The receiver's own selected bottle/label takes precedence. For a
            // gift-only portion retain an explicitly selected fancy bottle first.
            var representative=own.FirstOrDefault()??gifts.OrderByDescending(r=>r.Bottle?.Type==BottleType.Fancy)
                .ThenByDescending(r=>!string.IsNullOrWhiteSpace(r.LabelIdentityText)).ThenBy(r=>r.ConfirmedAt??r.CreatedAt).ThenBy(r=>r.Id).First();
            rows.Add(new(representative,portions.Sum(r=>r.VolumeMl),portions.Length,Notes(portions,representative)));
        }
        return rows;
    }

    private static Identity Read(string? rawId,string? rawUsername)
    {
        var id=rawId?.Trim()??"";var name=Normalize(rawUsername);
        if(long.TryParse(id,NumberStyles.None,CultureInfo.InvariantCulture,out var numeric)&&numeric>0)
            return new(numeric.ToString(CultureInfo.InvariantCulture),name);
        foreach(var prefix in new[]{"imported:","admin-username:"})
            if(id.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))return new("",name.Length>0?name:Normalize(id[prefix.Length..]));
        return new("",name);
    }
    private static string Normalize(string? value)=>value?.Trim().TrimStart('@').ToLowerInvariant()??"";
}
