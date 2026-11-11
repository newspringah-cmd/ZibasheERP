using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ZibasheERP.API.Tracking;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.AddressLabels;

public static class ShippingAddressDisplay
{
    private static string Characters(string value)
    {
        var text=new StringBuilder();
        foreach(var c in value.Normalize(NormalizationForm.FormKC))
        {
            if(c=='ـ'||char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)continue;
            if(c is '\u200c' or '\u200b'){text.Append(' ');continue;}
            if(char.GetUnicodeCategory(c)==UnicodeCategory.Format)continue;
            text.Append(c switch{'ي' or 'ى'=>'ی','ك'=>'ک',_=>c});
        }
        return text.ToString();
    }
    public static string RegisteredDate(DateTime utc)
    {
        var local=TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.SpecifyKind(utc,DateTimeKind.Utc),"Asia/Tehran");
        var calendar=new PersianCalendar();
        var date=FormattableString.Invariant($"{calendar.GetYear(local):0000}/{calendar.GetMonth(local):00}/{calendar.GetDayOfMonth(local):00}");
        return string.Concat(date.Select(c=>c is >= '0' and <= '9'?(char)('۰'+c-'0'):c));
    }
    public static string WithRegistrationDate(string text,DateTime utc)
    {
        var line="📅 تاریخ ثبت: "+RegisteredDate(utc);
        var split=text.IndexOf('\n');
        if(split<0)return text+"\n"+line;
        var rest=text[(split+1)..];
        if(rest.StartsWith("📅 تاریخ ثبت: ",StringComparison.Ordinal))
        {
            var end=rest.IndexOf('\n');
            return text[..(split+1)]+line+(end<0?string.Empty:rest[end..]);
        }
        return text[..(split+1)]+line+"\n"+rest;
    }
    public static string NormalizeName(string value)=>Regex.Replace(Characters(value),@"\s+"," ").Trim();
    private static bool IsCourtesy(string value)=>Regex.IsMatch(value,@"^(?:مرسی|ممنونم?|سپاسگزارم|سپاس|متشکرم|با تشکر|لطفا)(?:\s|$)");
    private static bool IsDisplayName(string value)=>value.Length is >=2 and <=100&&Regex.IsMatch(value,@"^[\p{L}\p{M} ]+$")&&
        !IsCourtesy(value)&&!Regex.IsMatch(value,@"مشتری|نامشخص|ناشناس|ثبت دستی|توسط|خیابان|کوچه|پلاک|بلوار|کد\s*پستی|تلفن|شماره|طبقه|واحد|آدرس|نشانی|استان|شهرستان");
    public static string? RecipientName(Address address)
    {
        string[] Candidates(string value)=>TrackingImportService.RecipientNameCandidatesForDisplay(Characters(value))
            .Select(NormalizeName).Where(n=>n.Length>0&&!IsCourtesy(n)&&n!=NormalizeName(address.City)&&n!=NormalizeName(address.Province)).Distinct().ToArray();
        var direct=NormalizeName(address.ReceiverName);
        if(IsDisplayName(direct))return direct;
        var candidates=Candidates(address.FullAddress);
        const string label=@"(?:نام\s*و\s*نام\s*خانوادگی|نام\s*گیرنده|نام\s*خانوادگی\s*و\s*نام|تحویل\s*گیرنده|گیرنده|برسد\s*به|به\s*دست|به\s*نام|بنام|نام(?=\s*[:：]))";
        var explicitNames=Regex.Matches(Characters(address.FullAddress),label+@"\s*[:：-]?\s*[^\r\n/،,;؛.]+")
            .Select(m=>Regex.Replace(m.Value,"^"+label+@"\s*[:：-]?\s*",string.Empty))
            .Select(value=>Regex.Split(value,@"[\p{N}]|(?:شماره\s*(?:تماس|همراه|موبایل)|کد\s*پستی|تلفن|موبایل|آدرس|ادرس|نشانی)")[0])
            .Select(NormalizeName).Where(IsDisplayName).Distinct().ToArray();
        if(explicitNames.Length>0)return explicitNames.Length==1?explicitNames[0]:null;
        return candidates.Length==1?candidates[0]:null;
    }
    public static string Identity(string identity,Address address)
    {
        var name=RecipientName(address);
        return string.IsNullOrEmpty(name)?identity:NormalizeName(identity)==name?name:identity+" — "+name;
    }
    public static string UpdateHeader(string message,Address address,string? manualRegistrar=null)
    {
        var name=RecipientName(address);
        var manual=new Regex(@"^ثبت‌کننده:[ \t]*(?<identity>[^\r\n]+)(?<legacy>\r?\nگیرندهٔ پستی:[^\r\n]*)?",RegexOptions.Multiline);
        if(manual.IsMatch(message))return manual.Replace(message,m=>
        {
            var identity=manualRegistrar??m.Groups["identity"].Value.Split(" — ",2,StringSplitOptions.None)[0].Trim();
            if(string.IsNullOrEmpty(name))return "ثبت‌کننده: "+identity+m.Groups["legacy"].Value;
            return "ثبت‌کننده: "+identity+" — گیرنده: "+name;
        },1);
        if(string.IsNullOrEmpty(name))return message;
        var header=new Regex(@"^(?<label>آیدی|مشتری|گیرندهٔ اصلی):[ \t]*(?<identity>[^\r\n]+)",RegexOptions.Multiline);
        if(header.IsMatch(message))return header.Replace(message,m=>
        {
            var identity=m.Groups["identity"].Value.Split(" — ",2,StringSplitOptions.None)[0].Trim();
            return m.Groups["label"].Value+": "+(NormalizeName(identity)==name?name:identity+" — "+name);
        },1);
        return message;
    }
}
