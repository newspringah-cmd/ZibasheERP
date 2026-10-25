using ZibasheERP.API.Telegram;

public static class SilentGroupTests
{
    public static void Run(Action<bool, string> check)
    {
        var options = new TelegramOptions { SilentGroupChatIds = "-1001283018740" };
        check(options.IsSilentGroup(new TelegramChat(-1001283018740, "supergroup")),
            "advertising discussion group is silent for incoming messages and callbacks");
        check(!options.IsSilentGroup(new TelegramChat(-100128301874, "supergroup")) &&
            !options.IsSilentGroup(new TelegramChat(-1004295475206, "supergroup")),
            "silent-group setting uses exact IDs and leaves operational groups active");
        check(!options.IsSilentGroup(new TelegramChat(-1001283018740, "channel")) &&
            !options.IsSilentGroup(new TelegramChat(123, "private")) && !options.IsSilentGroup(null),
            "silent-group setting cannot suppress sales channels or private admin chats");
        options.SilentGroupChatIds = "invalid; -1001283018740,\n-123";
        check(options.IsSilentGroup(new TelegramChat(-123, "group")),
            "silent-group configuration supports multiple exact IDs and ignores invalid values");
        options.SilentGroupChatIds = "";
        check(!options.IsSilentGroup(new TelegramChat(-1001283018740, "supergroup")),
            "empty setting preserves existing bot behavior");
    }
}
