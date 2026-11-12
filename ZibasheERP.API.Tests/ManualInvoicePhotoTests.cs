using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Application.Services;

public static class ManualInvoicePhotoTests
{
    public static async Task Run(Action<bool,string> check)
    {
        const long owner=741111222,chat=741111222;
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var sender=DispatchProxy.Create<ITelegramMessageSender,ManualInvoicePhotoSender>();var sent=(ManualInvoicePhotoSender)(object)sender;
        var store=new TelegramManualInvoiceDraftStore();
        var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
        foreach(var (name,value) in new(string,object)[]{("_sender",sender),("_manualInvoiceDrafts",store),("_options",new TelegramOptions{OwnerUserId=owner.ToString()}),
            ("_adminActivityContext",new AdminActivityContext()),("_logger",NullLogger<TelegramWebhookController>.Instance)})
            typeof(TelegramWebhookController).GetField(name,flags)!.SetValue(controller,value);
        async Task Call(string name,params object[] args)=>await (Task)typeof(TelegramWebhookController).GetMethod(name,flags)!.Invoke(controller,args)!;
        TelegramManualInvoiceDraft Draft(int count=2)
        {
            var d=new TelegramManualInvoiceDraft{ChatId=chat,UserId=owner,CustomerIdentity="@fixture",IsInventory=true,Stage=TelegramManualInvoiceStage.AwaitingPhoto};
            for(var i=0;i<count;i++)d.Lines.Add(new ManualInvoiceLineInput(i==0?"وانیلا اسموک <عطر>":"کشمیر اسکین",i+1,100000,0));
            store.Set(d);return d;
        }
        async Task Photo(string file,string? text=null)=>await Call("TryHandleManualInvoiceMessageAsync",
            new TelegramMessage(text,new(chat,"private"),new(owner,null),null,Photo:[new(file,100,100)],MessageId:100),CancellationToken.None);
        async Task Click(string data,long id,long actor=owner,long where=chat)=>await Call("TryHandleAdminCallbackAsync",
            new TelegramCallbackQuery("fixture",new(actor,null),new TelegramMessage(null,new(where,"private"),new(999,null,IsBot:true),null,MessageId:id),data),CancellationToken.None);
        var draft=Draft();await Photo("wrong-cashmere-upload");
        check(draft.Stage==TelegramManualInvoiceStage.AwaitingPhotoConfirmation&&draft.ProductPhotoFileIds.Count==0&&draft.Entries.Count==0,
            "manual invoice upload is pending until an explicit photo/name confirmation and cannot enter the invoice");
        var first=sent.Photos.Last();var token=draft.PendingPhotoToken;var confirm="invoicebatch:photook:"+token.ToString("N");
        check(first.Caption.Contains("وانیلا اسموک &lt;عطر&gt;")&&first.Caption.Contains("@fixture")&&first.Caption.Contains("1 میل")&&first.Caption.Contains("موجودی"),
            "confirmation displays the actual uploaded photo beside escaped perfume name, quantity, customer and stock tag");
        check(first.Rows.SelectMany(r=>r).All(b=>System.Text.Encoding.UTF8.GetByteCount(b.CallbackData!)<=64),"manual photo confirmation callbacks fit Telegram's byte limit");
        check(!store.TryBeginIssuing(chat,owner,out _),"an invoice cannot start issuing while its product photo awaits confirmation");
        await Click(confirm,first.Id+1);await Click(confirm,first.Id,owner+1);await Click(confirm,first.Id,where:chat+1);
        check(draft.ProductPhotoFileIds.Count==0&&draft.PendingPhotoToken==token,"wrong message, account and chat cannot approve a pending product photo");
        await Click("invoicebatch:manualfinish",first.Id);
        check(draft.Stage==TelegramManualInvoiceStage.AwaitingPhotoConfirmation&&draft.Entries.Count==0,"a stale Finish button cannot bypass product photo review");
        await Photo("second-upload-without-replace");
        check(sent.Photos.Count==1&&draft.PendingProductPhotoFileId=="wrong-cashmere-upload","new uploads cannot silently replace a photo awaiting confirmation");
        await Click("invoicebatch:photoredo:"+token.ToString("N"),first.Id);
        check(draft.Stage==TelegramManualInvoiceStage.AwaitingPhoto&&draft.ProductPhotoFileIds.Count==0&&draft.PendingProductPhotoFileId==null,
            "Change photo discards the wrong upload without committing it or creating an invoice");
        await Photo("correct-vanilla-upload");var corrected=sent.Photos.Last();var correctedToken=draft.PendingPhotoToken;
        await Click(confirm,first.Id);
        check(draft.ProductPhotoFileIds.Count==0&&draft.PendingPhotoToken==correctedToken,"stale confirmation of the replaced photo cannot confirm its replacement");
        await Click("invoicebatch:photook:"+correctedToken.ToString("N"),corrected.Id);
        check(draft.Stage==TelegramManualInvoiceStage.AwaitingPhoto&&draft.ProductPhotoFileIds.SequenceEqual(["correct-vanilla-upload"])&&draft.Entries.Count==0,
            "only the confirmed replacement is retained, and the next invoice line still requires its own photo");
        await Click("invoicebatch:photook:"+correctedToken.ToString("N"),corrected.Id);
        check(draft.ProductPhotoFileIds.Count==1,"replaying a successful photo confirmation cannot duplicate or advance another invoice line");
        await Photo("cashmere-for-second-line");var second=sent.Photos.Last();
        check(second.Caption.Contains("کشمیر اسکین")&&second.Caption.Contains("2 میل"),"the second photo preview names the second item and quantity rather than reusing the previous perfume");
        await Click("invoicebatch:photook:"+draft.PendingPhotoToken.ToString("N"),second.Id);
        check(draft.Stage==TelegramManualInvoiceStage.AwaitingMoreLines&&draft.Entries.Single().Photos.SequenceEqual(["correct-vanilla-upload","cashmere-for-second-line"])&&
            draft.Entries.Single().Lines.Select(l=>l.Description).SequenceEqual(draft.Lines.Select(l=>l.Description)),"confirmed product photos stay in the same order as the invoice lines through batch aggregation");
        check(sent.Texts.Any(t=>t.Contains("بررسی و تأیید شد")),"batch preview explicitly reports that every item photo was reviewed");
        draft=Draft(1);sent.FailPhoto=true;await Photo("failed-preview");sent.FailPhoto=false;
        check(draft.Stage==TelegramManualInvoiceStage.AwaitingPhoto&&draft.PendingPhotoToken==Guid.Empty&&draft.ProductPhotoFileIds.Count==0&&draft.Entries.Count==0,
            "failed photo preview cannot create an approvable draft or financial entry");
        await Photo("cancel-before-approval");await Photo("ignored","/cancel");
        check(!store.TryGet(chat,owner,out _),"cancel works while a photo awaits approval and leaves no issuable draft");
        draft=Draft(1);await Photo("expired-photo");var expired=sent.Photos.Last();var expiredToken=draft.PendingPhotoToken;draft.UpdatedAt=DateTime.UtcNow.AddMinutes(-21);
        await Click("invoicebatch:photook:"+expiredToken.ToString("N"),expired.Id);
        check(draft.ProductPhotoFileIds.Count==0&&!store.TryGet(chat,owner,out _),"expired photo confirmation cannot resurrect a manual invoice draft");
        draft=Draft(0);draft.Stage=TelegramManualInvoiceStage.AwaitingLine;
        foreach(var text in new[]{"@marsheem","@marsheem\n2\n100000"})
            await Call("TryHandleManualInvoiceMessageAsync",new TelegramMessage(text,new(chat,"private"),new(owner,null),null,MessageId:200),CancellationToken.None);
        check(draft.Stage==TelegramManualInvoiceStage.AwaitingLine&&draft.Lines.Count==0&&draft.PendingLineDescription=="",
            "customer username cannot become the perfume name in either stepwise or three-line manual entry");
        check(!ManualInvoiceInputRules.IsCustomerIdentityOnly("عطر هدیه برای @fixture")&&ManualInvoiceInputRules.IsCustomerIdentityOnly("https://t.me/fixture"),
            "identity-only name guard permits real product descriptions while recognizing a pasted Telegram profile link");
        var rejected=false;
        try { await new ZibasheERP.Infrastructure.Services.InvoiceIssuanceService(null!,null!).IssueManualAsync("@fixture",[new ManualInvoiceLineInput("@marsheem",1,100000)], ["photo"],owner.ToString()); }
        catch(InvalidOperationException e) { rejected=e.Message.Contains("آیدی مشتری جای نام عطر"); }
        check(rejected,"manual issuance service rejects identity-only perfume names before any database access or financial mutation");
    }
}

public class ManualInvoicePhotoSender:DispatchProxy
{
    public long NextId=700;public bool FailPhoto;public List<(long Id,string File,string Caption,IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> Rows)> Photos=[];public List<string> Texts=[];
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(method!.Name=="IsChatAdministratorAsync")return Task.FromResult(false);
        var id=++NextId;
        if(method.Name=="SendPhotoWithKeyboardAsync")
        {
            Photos.Add((id,(string)args![1]!, (string)args[2]!, (IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)args[3]!));
            return Task.FromResult(new TelegramSendResult(!FailPhoto,MessageId:FailPhoto?null:id));
        }
        if(args is {Length:>1}&&args[1] is string text)Texts.Add(text);
        return Task.FromResult(new TelegramSendResult(true,MessageId:id));
    }
}
