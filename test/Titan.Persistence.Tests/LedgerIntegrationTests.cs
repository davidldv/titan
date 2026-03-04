using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Testcontainers.PostgreSql;
using Titan.Engine;
using Titan.Persistence;
using Xunit;
using Marten;

namespace Titan.Persistence.Tests;

public sealed class LedgerIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:15-alpine")
        .WithDatabase("titantests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private Ledger _ledger = null!;
    private DocumentStore _store = null!;

    public async Task InitializeAsync()
    {
        // 1. Start the container dynamically
        await _postgres.StartAsync();

        // 2. Setup our ledger pointing to the ephemeral container
        _ledger = new Ledger(_postgres.GetConnectionString());
        
        // 3. Keep a raw Marten DocumentStore around just for test verifications
        _store = DocumentStore.For(opts =>
        {
            opts.Connection(_postgres.GetConnectionString());
            opts.Events.AddEventType<OrderPlacedEvent>();
            opts.Events.AddEventType<TradeExecutedEvent>();
        });
    }

    public async Task DisposeAsync()
    {
        _store?.Dispose();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task AppendEvents_SuccessfullyWritesToDatabase()
    {
        // Arrange
        var events = new List<OrderBookEvent>
        {
            new OrderPlacedEvent(
                SequenceNumber: 1,
                Timestamp: DateTimeOffset.UtcNow,
                OrderId: 100,
                TraderId: 5,
                Side: Side.Buy,
                Price: 1040,
                Quantity: 50
            ),
            new TradeExecutedEvent(
                SequenceNumber: 2,
                Timestamp: DateTimeOffset.UtcNow,
                MakerOrderId: 90,
                TakerOrderId: 100,
                ExecutedPrice: 1040,
                ExecutedQuantity: 25
            )
        };

        // Act
        await _ledger.AppendEventsAsync(events);

        // Assert
        // Verify via Marten that exactly 2 events were written to the specific Guid stream.
        await using var session = _store.LightweightSession();
        var streamId = new Guid("11111111-1111-1111-1111-111111111111");
        
        var appendedEvents = await session.Events.FetchStreamAsync(streamId);
        
        Assert.Equal(2, appendedEvents.Count);
        
        var placed = Assert.IsType<OrderPlacedEvent>(appendedEvents[0].Data);
        Assert.Equal(100, placed.OrderId);
        Assert.Equal(5, placed.TraderId);

        var trade = Assert.IsType<TradeExecutedEvent>(appendedEvents[1].Data);
        Assert.Equal(90, trade.MakerOrderId);
        Assert.Equal(25, trade.ExecutedQuantity);
    }
}
