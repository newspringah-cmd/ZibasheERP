using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ZibasheERP.API.Telegram;
public static class SourcePhotoReadTests
{
    public static async Task Run(Action<bool,string> check)
    {
        using var sender=new TelegramMessageSender(Options.Create(new TelegramOptions{BotToken="fixture"}));
        var field=typeof(TelegramMessageSender).GetField("_httpClient",BindingFlags.Instance|BindingFlags.NonPublic)!;
        ((HttpClient)field.GetValue(sender)!).Dispose();var handler=new SourcePhotoHandler();field.SetValue(sender,new HttpClient(handler){BaseAddress=new Uri("https://example.invalid/")});
        var result=await sender.ReadSourcePhotoAsync("771234567","-1001236168661",123);
        check(result.IsSuccessful&&result.ExternalFileId=="source-photo-large","source-post photo reader extracts the original Telegram image ID");
        check(handler.Calls.SequenceEqual(["forwardMessage","deleteMessage"])&&handler.From=="-1001236168661"&&handler.Post==123,
            "source-post read uses exact original post and removes its temporary private copy");
        var denied=await sender.ReadSourcePhotoAsync("-100123","-1001236168661",123);
        check(!denied.IsSuccessful&&handler.Calls.Count==2,"photo extraction cannot forward a preview into a public group or channel");
    }
}
public sealed class SourcePhotoHandler:HttpMessageHandler
{
    public List<string> Calls=[];public string? From;public long Post;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        var method=request.RequestUri!.Segments.Last();Calls.Add(method);
        var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        if(method=="forwardMessage"){From=body.RootElement.GetProperty("from_chat_id").GetString();Post=body.RootElement.GetProperty("message_id").GetInt64();}
        return new(HttpStatusCode.OK){Content=new StringContent(method=="forwardMessage"?"{\"ok\":true,\"result\":{\"message_id\":88,\"photo\":[{\"file_id\":\"source-photo-small\"},{\"file_id\":\"source-photo-large\"}]}}":"{\"ok\":true,\"result\":true}")};
    }
}
