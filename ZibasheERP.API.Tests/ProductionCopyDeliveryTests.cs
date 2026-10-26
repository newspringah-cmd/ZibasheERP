using System.Collections.Concurrent;
using System.Reflection;
using ZibasheERP.API.Telegram;

public static class ProductionCopyDeliveryTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var listId = Guid.NewGuid();
        var store = new ProductionCopyReceiptTestStore();
        var sender = DispatchProxy.Create<ITelegramMessageSender, ProductionCopySenderTestProxy>();
        var proxy = (ProductionCopySenderTestProxy)(object)sender;
        var service = new TelegramProductionCopyDelivery(store, sender);
        var rows = new IReadOnlyCollection<TelegramInlineButton>[]
        {
            new[] { new TelegramInlineButton("logo", "plogo:set:test") }
        };
        Task<TelegramSendResult> Send(TelegramProductionCopyDelivery target, string? body = "label") =>
            target.SendAsync(listId, " -1001 ", "photo", "header", rows, body!, default);

        var first = await Send(service);
        check(first.IsSuccessful && proxy.Calls == 2 && (await store.GetAsync(
            TelegramProductionCopyDelivery.CreateKey(listId, "-1001"), default))?.Complete == true,
            "label delivery saves the photo header and all label text parts");
        await Send(service);
        await Send(new TelegramProductionCopyDelivery(store, sender));
        check(proxy.Calls == 2, "a list cycle is never sent twice to the same print group");

        store = new ProductionCopyReceiptTestStore();
        service = new TelegramProductionCopyDelivery(store, sender);
        proxy.Calls = 0;
        proxy.FailBody = true;
        var partial = await Send(service);
        var held = await Send(service);
        var receipt = await store.GetAsync(TelegramProductionCopyDelivery.CreateKey(listId, "-1001"), default);
        check(!partial.IsSuccessful && !held.IsSuccessful && proxy.Calls == 2 &&
            receipt?.HeaderMessageId == 100 && receipt.Error is not null,
            "uncertain partial label delivery preserves its receipt and blocks duplicate printing");

        check(TelegramProductionCopyDelivery.CreateKey(listId, "-1001") ==
                TelegramProductionCopyDelivery.CreateKey(listId, " -1001 ") &&
            TelegramProductionCopyDelivery.CreateKey(listId, "-1001") !=
                TelegramProductionCopyDelivery.CreateKey(Guid.NewGuid(), "-1001") &&
            TelegramProductionCopyDelivery.CreateKey(listId, "-1001") !=
                TelegramProductionCopyDelivery.CreateKey(listId, "-1002"),
            "print idempotency is keyed by list cycle and destination chat");
    }
}

public sealed class ProductionCopyReceiptTestStore : IProductionCopyDeliveryStore
{
    private readonly ConcurrentDictionary<Guid, ProductionCopyDeliveryReceipt> receipts = new();
    public Task<bool> TryBeginAsync(Guid key, Guid listId, string chatId, CancellationToken ct) =>
        Task.FromResult(receipts.TryAdd(key, new(listId)));
    public Task<ProductionCopyDeliveryReceipt?> GetAsync(Guid key, CancellationToken ct) =>
        Task.FromResult(receipts.TryGetValue(key, out var receipt) ? receipt : null);
    public Task SaveAsync(Guid key, ProductionCopyDeliveryReceipt receipt, CancellationToken ct)
    {
        receipts[key] = receipt;
        return Task.CompletedTask;
    }
}

public class ProductionCopySenderTestProxy : DispatchProxy
{
    public int Calls;
    public bool FailBody;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        Calls++;
        if (targetMethod!.Name is nameof(ITelegramMessageSender.SendPhotoWithKeyboardAsync) or
            nameof(ITelegramMessageSender.SendInlineKeyboardAsync))
            return Task.FromResult(new TelegramSendResult(true, MessageId: 100));
        if (targetMethod.Name == nameof(ITelegramMessageSender.SendAsync))
            return Task.FromResult(FailBody
                ? new TelegramSendResult(false, "temporary failure")
                : new TelegramSendResult(true, MessageId: 101));
        throw new NotSupportedException(targetMethod.Name);
    }
}
