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
    public static string NormalizeName(string value)=>Regex.Replace(Characters(value),@"\s+"," ").Trim();
    public static string? RecipientName(Address address)
    {
        string[] Candidates(string value)=>TrackingImportService.RecipientNameCandidatesForDisplay(Characters(value))
            .Select(NormalizeName).Where(n=>n.Length>0&&n!=NormalizeName(address.City)&&n!=NormalizeName(address.Province)).Distinct().ToArray();
        var direct=NormalizeName(address.ReceiverName);
        if(direct.Length is >=2 and <=100&&Regex.IsMatch(direct,@"^[\p{L}\p{M} ]+$")&&
            !Regex.IsMatch(direct,@"مشتری|نامشخص|ناشناس|ثبت دستی|توسط|خیابان|کوچه|پلاک|بلوار|کد\s*پستی|تلفن|شماره|طبقه|واحد|آدرس|نشانی|استان|شهرستان"))return direct;
        var candidates=Candidates(address.FullAddress);
        const string label=@"(?:نام\s*و\s*نام\s*خانوادگی|نام\s*گیرنده|نام\s*خانوادگی\s*و\s*نام|تحویل\s*گیرنده|گیرنده|برسد\s*به|به\s*دست|به\s*نام|بنام|نام(?=\s*[:：]))";
        var explicitNames=Regex.Matches(Characters(address.FullAddress),label+@"\s*[:：-]?\s*[^\r\n/،,;؛.]+")
            .SelectMany(m=>Candidates(m.Value)).Distinct().ToArray();
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
