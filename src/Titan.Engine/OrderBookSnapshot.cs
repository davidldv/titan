namespace Titan.Engine;

public readonly record struct OrderBookSnapshotLevel(long Price, long TotalQuantity);

public sealed record OrderBookSnapshot(
    OrderBookSnapshotLevel[] Bids,
    OrderBookSnapshotLevel[] Asks
);
