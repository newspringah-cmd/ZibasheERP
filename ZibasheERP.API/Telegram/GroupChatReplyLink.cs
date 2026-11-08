using System.Globalization;
using System.Text.RegularExpressions;

namespace ZibasheERP.API.Telegram;

public static class GroupChatReplyLink
{
    public static bool TryParse(string text,string configuredChat,out long messageId)
    {
        messageId=0;
        if(!long.TryParse(configuredChat,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out var target)||target>=0||
            !Uri.TryCreate(text.Trim(),UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host!="t.me"||!uri.IsDefaultPort||uri.UserInfo.Length!=0)
            return false;
        var match=Regex.Match(uri.AbsolutePath,@"^/c/([1-9][0-9]{0,14})/([1-9][0-9]{0,9})/?$");
        return match.Success&&long.TryParse("-100"+match.Groups[1].Value,out var chat)&&chat==target&&
            long.TryParse(match.Groups[2].Value,out messageId)&&messageId<=int.MaxValue;
    }
    public static string Url(string chat,long message)=>chat.StartsWith("-100",StringComparison.Ordinal)&&chat.Length>4&&message>0?$"https://t.me/c/{chat[4..]}/{message}":string.Empty;
}
