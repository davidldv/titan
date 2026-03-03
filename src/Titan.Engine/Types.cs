namespace Titan.Engine;

public enum Side : byte
{
    Buy = 0,
    Sell = 1,
}

public readonly record struct Order(
    long OrderId,
    int TraderId,
    Side Side,
    long Price,
    long Quantity
);

public enum RejectReason : byte
{
    None = 0,
    InsufficientFunds = 1,
    CapacityExceeded = 2,
    Invalid = 3,
}

public readonly record struct MatchResult(
    bool Accepted,
    RejectReason RejectReason,
    long FilledQuantity,
    long RemainingQuantity,
    int Trades
);
