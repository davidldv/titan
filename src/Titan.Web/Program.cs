using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Titan.Engine;
using Titan.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddSingleton<BalanceSheet>(sp => 
{
    var bs = new BalanceSheet(1000);
    for (int i = 0; i < 1000; i++) bs.AddAccount(1_000_000, 1_000_000_000_000);
    return bs;
});
builder.Services.AddSingleton<OrderBook>(sp => new OrderBook(sp.GetRequiredService<BalanceSheet>(), 100_000, 1_000));
builder.Services.AddSingleton<Channel<Order>>(_ => Channel.CreateUnbounded<Order>(new UnboundedChannelOptions { SingleReader = true }));
builder.Services.AddHostedService<EngineLoop>();

var app = builder.Build();

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
    
    public class BroadcasterHub : Hub {}
    
    public class EngineLoop : BackgroundService
    {
        private readonly OrderBook _book;
        private readonly Channel<Order> _channel;
        private readonly IHubContext<BroadcasterHub> _hub;

        public EngineLoop(OrderBook book, Channel<Order> channel, IHubContext<BroadcasterHub> hub)
        {
            _book = book;
            _channel = channel;
            _hub = hub;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await foreach (var order in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                var result = _book.Match(order);
                // Broadcast snapshot every batch or time interval ideally, but for demo:
                var snapshot = _book.GetSnapshot(10);
                await _hub.Clients.All.SendAsync("OrderBookUpdated", snapshot, result);
            }
        }
    }
}
