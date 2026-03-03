using Marten;

namespace Titan.Persistence;

public sealed class Ledger
{
    private readonly IDocumentStore _store;

    public Ledger(string connectionString)
    {
        _store = DocumentStore.For(opts =>
        {
            opts.Connection(connectionString);
            
            // Register event types so Marten knows how to deserialize them
            opts.Events.AddEventType<OrderPlacedEvent>();
            opts.Events.AddEventType<OrderRejectedEvent>();
            opts.Events.AddEventType<TradeExecutedEvent>();
        });
    }

    public async Task AppendEventsAsync(IReadOnlyList<OrderBookEvent> events, CancellationToken cancellationToken = default)
    {
        if (events.Count == 0) return;

        await using var session = _store.LightweightSession();
        
        // We use a constant stream ID for the single matching engine instance. 
        // In a real system, this would be partitioned by symbol/ticker (e.g. BTC-USD).
        var streamId = new Guid("11111111-1111-1111-1111-111111111111");

        session.Events.Append(streamId, events);
        await session.SaveChangesAsync(cancellationToken);
    }
}