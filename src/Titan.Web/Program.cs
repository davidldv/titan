using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Testcontainers.PostgreSql;
using Titan.Engine;
using Titan.Persistence;
using Titan.Web;

var builder = WebApplication.CreateBuilder(args);

var postgres = new PostgreSqlBuilder()
    .WithImage("postgres:15-alpine")
    .WithDatabase("titan")
    .WithUsername("postgres")
    .WithPassword("postgres")
    .Build();

await postgres.StartAsync();

builder.Services.AddSignalR();
builder.Services.AddSingleton<BalanceSheet>(_ =>
{
    var bs = new BalanceSheet(1000);
    for (int i = 0; i < 1000; i++) bs.AddAccount(1_000_000, 1_000_000_000_000);
    return bs;
});
builder.Services.AddSingleton<OrderBook>(sp => new OrderBook(sp.GetRequiredService<BalanceSheet>(), 100_000, 1_000));
builder.Services.AddSingleton<Channel<Order>>(_ => Channel.CreateUnbounded<Order>(new UnboundedChannelOptions { SingleReader = true }));
builder.Services.AddSingleton<Ledger>(_ => new Ledger(postgres.GetConnectionString()));
builder.Services.AddHostedService<EngineLoop>();

var app = builder.Build();

app.Lifetime.ApplicationStopping.Register(() => postgres.DisposeAsync().AsTask().GetAwaiter().GetResult());

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<BroadcasterHub>("/hub");

app.MapPost("/api/orders", async (OrderRequest req, Channel<Order> channel) =>
{
    var order = new Order(
        OrderId: Interlocked.Increment(ref Globals.OrderCounter),
        TraderId: req.TraderId,
        Side: req.Side.Equals("Buy", StringComparison.OrdinalIgnoreCase) ? Side.Buy : Side.Sell,
        Price: req.Price,
        Quantity: req.Quantity
    );
    await channel.Writer.WriteAsync(order);
    return Results.Accepted();
});

app.Run();

namespace Titan.Web
{
    public static class Globals { public static long OrderCounter; }
    public record OrderRequest(int TraderId, string Side, long Price, long Quantity);

    public class BroadcasterHub : Hub { }

    public class EngineLoop : BackgroundService
    {
        private readonly OrderBook _book;
        private readonly Channel<Order> _channel;
        private readonly IHubContext<BroadcasterHub> _hub;
        private readonly Ledger _ledger;
        private long _seq;

        public EngineLoop(OrderBook book, Channel<Order> channel, IHubContext<BroadcasterHub> hub, Ledger ledger)
        {
            _book = book;
            _channel = channel;
            _hub = hub;
            _ledger = ledger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var events = new List<OrderBookEvent>(8);
            var trades = new List<Trade>(8);

            await foreach (var order in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                events.Clear();
                trades.Clear();
                var now = DateTimeOffset.UtcNow;

                events.Add(new OrderPlacedEvent(
                    Interlocked.Increment(ref _seq), now,
                    order.OrderId, order.TraderId, order.Side, order.Price, order.Quantity));

                var result = _book.Match(order, trades);

                if (!result.Accepted)
                {
                    events.Add(new OrderRejectedEvent(
                        Interlocked.Increment(ref _seq), now, order.OrderId, result.RejectReason));
                }
                else
                {
                    foreach (var t in trades)
                    {
                        events.Add(new TradeExecutedEvent(
                            Interlocked.Increment(ref _seq), now,
                            t.MakerOrderId, t.TakerOrderId, t.Price, t.Quantity));
                    }
                }

                await _ledger.AppendEventsAsync(events, stoppingToken);

                var snapshot = _book.GetSnapshot(10);
                await _hub.Clients.All.SendAsync("OrderBookUpdated", snapshot, result, cancellationToken: stoppingToken);
            }
        }
    }
}
