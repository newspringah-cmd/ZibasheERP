using System.Text.RegularExpressions;
namespace ZibasheERP.API.AddressLabels;

public static class ShippingRecipientNameSimilarity
{
    private static string Key(string name)=>Regex.Replace(ShippingAddressDisplay.NormalizeName(name).Replace('آ','ا').Replace('أ','ا').Replace('إ','ا').Replace('ٱ','ا').Replace('ئ','ی').Replace('ؤ','و'),@"[^\p{L}\p{M}]",string.Empty).ToLowerInvariant();
    private static int Distance(string left,string right,int limit)
    {
        if(Math.Abs(left.Length-right.Length)>limit)return limit+1;
        var previous=Enumerable.Range(0,right.Length+1).ToArray();
        for(var i=1;i<=left.Length;i++)
        {
            var current=new int[right.Length+1];current[0]=i;var minimum=i;
            for(var j=1;j<=right.Length;j++){current[j]=Math.Min(Math.Min(previous[j]+1,current[j-1]+1),previous[j-1]+(left[i-1]==right[j-1]?0:1));minimum=Math.Min(minimum,current[j]);}
            if(minimum>limit)return limit+1;previous=current;
        }
        return previous[right.Length];
    }
    public static bool Similar(string left,string right)
    {
        if(left.Contains('@')||right.Contains('@'))return false;
        var a=Key(left);var b=Key(right);if(Math.Min(a.Length,b.Length)<2)return false;
        if(a==b)return true;
        if(Math.Min(a.Length,b.Length)>=3&&Distance(a,b,1)<=1)return true;
        var aw=Regex.Matches(ShippingAddressDisplay.NormalizeName(left),@"[\p{L}\p{M}]+").Select(m=>Key(m.Value)).ToArray();
        var bw=Regex.Matches(ShippingAddressDisplay.NormalizeName(right),@"[\p{L}\p{M}]+").Select(m=>Key(m.Value)).ToArray();
        return aw.Length>=2&&bw.Length>=2&&Math.Min(a.Length,b.Length)>=9&&aw.Any(w=>w.Length>=3&&bw.Contains(w))&&Distance(a,b,2)<=2;
    }
}
