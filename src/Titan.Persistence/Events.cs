namespace Titan.Persistence;

public enum EventType : byte
{
    OrderPlaced = 1,
    OrderRejected = 2,
    TradeExecuted = 3
}

public abstract record OrderBookEvent(long SequenceNumber, DateTimeOffset Timestamp);

public sealed record OrderPlacedEvent(
    long SequenceNumber,
    DateTimeOffset Timestamp,
    long OrderId,
    int TraderId,
    Engine.Side Side,
    long Price,
    long Quantity
) : OrderBookEvent(SequenceNumber, Timestamp);

// Represents either full or partial rejection (e.g. insufficient funds)
public sealed record OrderRejectedEvent(
    long SequenceNumber,
    DateTimeOffset Timestamp,
    long OrderId,
    Engine.RejectReason Reason
) : OrderBookEvent(SequenceNumber, Timestamp);

public sealed record TradeExecutedEvent(
    long SequenceNumber,
    DateTimeOffset Timestamp,
    long MakerOrderId,
    long TakerOrderId,
    long ExecutedPrice,
    long ExecutedQuantity
) : OrderBookEvent(SequenceNumber, Timestamp);
