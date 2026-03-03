using System.Runtime.CompilerServices;

namespace Titan.Engine;

public sealed class BalanceSheet
{
    private int _count;

    public BalanceSheet(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));

        AvailableBase = new long[capacity];
        LockedBase = new long[capacity];
        AvailableQuote = new long[capacity];
        LockedQuote = new long[capacity];
    }

    public int Count => _count;

    public long[] AvailableBase { get; }
    public long[] LockedBase { get; }
    public long[] AvailableQuote { get; }
    public long[] LockedQuote { get; }

    public int AddAccount(long initialBase, long initialQuote)
    {
        int id = _count;
        if ((uint)id >= (uint)AvailableBase.Length)
        {
            throw new InvalidOperationException("BalanceSheet capacity exceeded.");
        }

        AvailableBase[id] = initialBase;
        AvailableQuote[id] = initialQuote;
        LockedBase[id] = 0;
        LockedQuote[id] = 0;

        _count = id + 1;
        return id;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryLockFor(in Order order)
    {
        if (order.Quantity <= 0 || order.Price <= 0) return false;
        if ((uint)order.TraderId >= (uint)_count) return false;

        int t = order.TraderId;

        if (order.Side == Side.Buy)
        {
            long requiredQuote;
            try
            {
                checked { requiredQuote = order.Quantity * order.Price; }
            }
            catch (OverflowException)
            {
                return false;
            }

            long available = AvailableQuote[t];
            if (available < requiredQuote) return false;

            AvailableQuote[t] = available - requiredQuote;
            LockedQuote[t] += requiredQuote;
            return true;
        }
        else
        {
            long requiredBase = order.Quantity;
            long available = AvailableBase[t];
            if (available < requiredBase) return false;

            AvailableBase[t] = available - requiredBase;
            LockedBase[t] += requiredBase;
            return true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SettleTrade(
        int buyerId,
        int sellerId,
        long quantity,
        long executionPrice,
        long buyerLimitPrice
    )
    {
        // Buyer receives base.
        AvailableBase[buyerId] += quantity;

        // Seller delivers base from locked.
        LockedBase[sellerId] -= quantity;

        // Quote transfer uses execution price.
        long notionalAtExec = checked(quantity * executionPrice);
        long buyerLockedDecrease = checked(quantity * buyerLimitPrice);

        // Buyer spends from locked quote at their limit; any difference is price improvement back to available.
        LockedQuote[buyerId] -= buyerLockedDecrease;
        AvailableQuote[buyerId] += (buyerLockedDecrease - notionalAtExec);

        // Seller receives quote.
        AvailableQuote[sellerId] += notionalAtExec;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReleaseUnfilled(in Order order, long remainingQuantity)
    {
        if (remainingQuantity <= 0) return;

        int t = order.TraderId;

        if (order.Side == Side.Buy)
        {
            long refund = checked(remainingQuantity * order.Price);
            LockedQuote[t] -= refund;
            AvailableQuote[t] += refund;
        }
        else
        {
            long refund = remainingQuantity;
            LockedBase[t] -= refund;
            AvailableBase[t] += refund;
        }
    }
}
