using ZibasheERP.Application.Features.Orders.CreateOrder;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Orders;

public sealed class CreateOrderCommandHandlerTests
{
    [Fact]
    public async Task Handle_WithTelegramCustomer_CreatesOrderAndUpdatesBalances()
    {
        var fixture = new Fixture();
        var command = fixture.ValidTelegramCommand();

        var orderId = await fixture.Handler.Handle(command, CancellationToken.None);
        var addedOrder = fixture.Orders.AddedOrder
            ?? throw new InvalidOperationException("Order was not added.");

        Assert.NotEqual(Guid.Empty, orderId);
        Assert.Equal(fixture.Customer.Id, addedOrder.CustomerId);
        Assert.Equal(10, fixture.SalesList.ReservedVolume);
        Assert.Equal(4_500_000m, fixture.Customer.CurrentDebt);
        Assert.True(fixture.Orders.SaveChangesCalled);
    }

    [Fact]
    public async Task Validator_RequiresExactlyOneCustomerIdentifier()
    {
        var fixture = new Fixture();
        var validator = new CreateOrderValidator();
        var command = fixture.ValidTelegramCommand();
        command.CustomerId = fixture.Customer.Id;

        var result = await validator.ValidateAsync(command);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.ErrorMessage.Contains("دقیقاً یکی"));
    }

    [Fact]
    public async Task Handle_WithInsufficientCredit_RejectsOrder()
    {
        var fixture = new Fixture();
        fixture.Customer.WalletBalance = 0;
        fixture.Customer.CreditLimit = 100;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Handler.Handle(
                fixture.ValidTelegramCommand(),
                CancellationToken.None));

        Assert.Contains("اعتبار مشتری کافی نیست", exception.Message);
        Assert.Null(fixture.Orders.AddedOrder);
    }

    [Fact]
    public async Task Handle_WithInsufficientVolume_RejectsOrder()
    {
        var fixture = new Fixture();
        fixture.SalesList.ReservedVolume = 95;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Handler.Handle(
                fixture.ValidTelegramCommand(),
                CancellationToken.None));

        Assert.Contains("حجم کافی موجود نیست", exception.Message);
        Assert.Null(fixture.Orders.AddedOrder);
    }

    [Fact]
    public async Task Handle_WhenBottleOwnerAlreadyExists_RejectsSecondOwner()
    {
        var fixture = new Fixture();
        fixture.SalesList.HasBottleOwner = true;
        fixture.SalesList.BottleOwnerCustomerId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Handler.Handle(
                fixture.ValidTelegramCommand(),
                CancellationToken.None));

        Assert.Contains("صاحب باتل این لیست قبلاً مشخص شده است", exception.Message);
        Assert.Null(fixture.Orders.AddedOrder);
    }

    private sealed class Fixture
    {
        public Customer Customer { get; } = new()
        {
            Id = Guid.NewGuid(),
            FullName = "Telegram Customer",
            Mobile = "09120000000",
            TelegramId = "123456789",
            WalletBalance = 10_000_000,
            CreditLimit = 0,
            CurrentDebt = 0,
            CanPlaceOrder = true
        };

        public SalesList SalesList { get; }
        public FakeOrderRepository Orders { get; } = new();
        public CreateOrderCommandHandler Handler { get; }

        public Fixture()
        {
            var perfume = new Perfume
            {
                Id = Guid.NewGuid(),
                Name = "Test Perfume",
                Brand = "Test Brand",
                IsActive = true
            };
            var batch = new Batch
            {
                Id = Guid.NewGuid(),
                PerfumeId = perfume.Id,
                Perfume = perfume
            };
            SalesList = new SalesList
            {
                Id = Guid.NewGuid(),
                BatchId = batch.Id,
                PricePerMl = 450_000,
                TotalVolume = 100,
                Status = SalesListStatus.Open
            };
            var bottle = new Bottle
            {
                Id = Guid.NewGuid(),
                Name = "10ml Bottle",
                VolumeMl = 10,
                Type = BottleType.Normal,
                SalePrice = 100_000,
                IsActive = true
            };

            Handler = new CreateOrderCommandHandler(
                new FakeCustomerRepository(Customer),
                Orders,
                new FakeSalesListRepository(SalesList),
                new FakeBottleRepository(bottle),
                new FakeBatchRepository(batch));
        }

        public CreateOrderCommand ValidTelegramCommand() => new()
        {
            TelegramId = Customer.TelegramId,
            SalesListId = SalesList.Id,
            RequestedVolumeMl = 10,
            IsBottleOwner = true
        };
    }

    private sealed class FakeCustomerRepository(Customer customer) : ICustomerRepository
    {
        public Task<Customer?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Customer?>(customer.Id == id ? customer : null);

        public Task<Customer?> GetByTelegramIdAsync(string telegramId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Customer?>(customer.TelegramId == telegramId ? customer : null);

        public Task<Customer?> GetByMobileAsync(string mobile, CancellationToken cancellationToken = default) =>
            Task.FromResult<Customer?>(customer.Mobile == mobile ? customer : null);

        public Task<Customer?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default) =>
            Task.FromResult<Customer?>(customer.Username == username ? customer : null);

        public Task AddAsync(Customer value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(Customer value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeOrderRepository : IOrderRepository
    {
        public Order? AddedOrder { get; private set; }
        public bool SaveChangesCalled { get; private set; }

        public Task AddAsync(Order order, CancellationToken cancellationToken = default)
        {
            AddedOrder = order;
            return Task.CompletedTask;
        }

        public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<Order?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<Order?> GetByOrderNumberAsync(string orderNumber, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<IReadOnlyCollection<Order>> GetByCustomerIdAsync(Guid customerId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Order>>(Array.Empty<Order>());
        public Task<bool> OrderNumberExistsAsync(string orderNumber, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task UpdateAsync(Order order, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSalesListRepository(SalesList salesList) : ISalesListRepository
    {
        public Task<IReadOnlyCollection<SalesList>> GetOpenAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<SalesList>>(Array.Empty<SalesList>());

        public Task<SalesList?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<SalesList?>(salesList.Id == id ? salesList : null);

        public Task UpdateAsync(SalesList value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeBottleRepository(Bottle bottle) : IBottleRepository
    {
        public Task<Bottle?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Bottle?>(bottle.Id == id ? bottle : null);

        public Task<Bottle?> GetByTypeAsync(BottleType type, CancellationToken cancellationToken = default) =>
            Task.FromResult<Bottle?>(bottle.Type == type ? bottle : null);

        public Task<List<Bottle>> GetActiveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<Bottle> { bottle });
    }

    private sealed class FakeBatchRepository(Batch batch) : IBatchRepository
    {
        public Task<Batch?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Batch?>(batch.Id == id ? batch : null);

        public Task UpdateAsync(Batch value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
