using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ZibasheERP.Infrastructure.Services;

public sealed record InvoiceCaptionIdentity(string? Username,string? Number,decimal Amount,bool Inventory,string[] Names,decimal[] Volumes,DateTime? FromUtc,DateTime? UntilUtc)
{
    public static string Normalize(string value)
    {
        var b=new StringBuilder();
        foreach(var c in value.Replace('ي','ی').Replace('ك','ک').Replace('ى','ی').Normalize(NormalizationForm.FormD))
        {
            if(CharUnicodeInfo.GetUnicodeCategory(c)==UnicodeCategory.NonSpacingMark)continue;
            b.Append(c is >= '۰' and <= '۹'?(char)('0'+c-'۰'):c is >= '٠' and <= '٩'?(char)('0'+c-'٠'):char.IsLetterOrDigit(c)?char.ToLowerInvariant(c):' ');
        }
        return Regex.Replace(b.ToString(),@"\s+"," ").Trim();
    }
    public static string Digits(string value)=>string.Concat(value.Select(c=>c is >= '۰' and <= '۹'?(char)('0'+c-'۰'):c is >= '٠' and <= '٩'?(char)('0'+c-'٠'):c));
    public static InvoiceCaptionIdentity Parse(string caption)
    {
        if(caption.Length is <15 or >16000)throw new InvalidOperationException("کپشن کامل فاکتور را بفرستید.");
        var text=Digits(caption).Replace("\r","").Replace("\u200e","").Replace("\u200f","");
        var user=Regex.Match(text,@"(?:فاکتور[^\n@]*|مشتری\s*[:：]?)@([A-Za-z0-9_]{5,32})(?![A-Za-z0-9_])",RegexOptions.IgnoreCase);
        var amount=Regex.Match(text,@"(?:مبلغ\s+کل\s+فاکتور|مبلغ\s+قابل\s+پرداخت|جمع\s+عطر\s+و\s+شیشه)\s*[:：]\s*([0-9,٬.\s]+)\s*تومان");
        var number=Regex.Match(text,@"\bINV-\d{6}-\d{4}\b",RegexOptions.IgnoreCase);
        if((!user.Success&&!number.Success)||!amount.Success||!decimal.TryParse(amount.Groups[1].Value.Replace(",","").Replace("٬","").Trim(),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var total)||total<=0)
            throw new InvalidOperationException("کپشن باید شمارهٔ فاکتور یا آیدی مشتری و «مبلغ قابل پرداخت» مثبت داشته باشد؛ رسید هدیهٔ صفرمبلغ فاکتور مالی نیست.");
        var names=Regex.Matches(text,@"(?:🧴|نام\s+فارسی\s*[:：])\s*([^\n]+)").Select(m=>Normalize(m.Groups[1].Value)).ToArray();
        var volumes=Regex.Matches(text,@"مقدار\s*[:：]\s*([0-9.٫]+)\s*میل").Select(m=>decimal.Parse(m.Groups[1].Value.Replace('٫','.'),CultureInfo.InvariantCulture)).ToArray();
        DateTime? from=null,until=null;
        if(!number.Success)
        {
            var date=Regex.Match(text,@"تاریخ(?:\s+شمسی)?\s*[:：]\s*(\d{4})[/-](\d{1,2})[/-](\d{1,2})");
            if(!date.Success||names.Length==0||volumes.Length==0)throw new InvalidOperationException("برای فاکتور بدون شماره، کپشن کامل شامل تاریخ، نام عطر، مقدار و مبلغ را بفرستید.");
            try
            {
                var y=int.Parse(date.Groups[1].Value);var m=int.Parse(date.Groups[2].Value);var d=int.Parse(date.Groups[3].Value);
                var day=y<1700?new PersianCalendar().ToDateTime(y,m,d,0,0,0,0):new DateTime(y,m,d);
                var zone=TimeZoneInfo.FindSystemTimeZoneById("Asia/Tehran");from=TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day,DateTimeKind.Unspecified),zone);until=TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.AddDays(1),DateTimeKind.Unspecified),zone);
            }
            catch(ArgumentException){throw new InvalidOperationException("تاریخ کپشن معتبر نیست.");}
        }
        return new(user.Success?user.Groups[1].Value.ToLowerInvariant():null,number.Success?number.Value.ToUpperInvariant():null,total,caption.Contains("🔴🔴موجودی🔴🔴"),names,volumes,from,until);
    }
}
