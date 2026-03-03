using System.Runtime.CompilerServices;

namespace Titan.Engine;

public sealed class OrderBook
{
    private const int None = -1;

    private readonly BalanceSheet _balances;

    private readonly BookOrderNode[] _nodes;
    private int _freeHead;

    private readonly PriceLevel[] _bids; // descending
    private int _bidLevels;

    private readonly PriceLevel[] _asks; // ascending
    private int _askLevels;

    private long _nextInternalOrderId;

    public OrderBook(BalanceSheet balances, int maxRestingOrders, int maxPriceLevelsPerSide)
    {
        _balances = balances ?? throw new ArgumentNullException(nameof(balances));
        if (maxRestingOrders <= 0) throw new ArgumentOutOfRangeException(nameof(maxRestingOrders));
        if (maxPriceLevelsPerSide <= 0) throw new ArgumentOutOfRangeException(nameof(maxPriceLevelsPerSide));

        _nodes = new BookOrderNode[maxRestingOrders];
        _bids = new PriceLevel[maxPriceLevelsPerSide];
        _asks = new PriceLevel[maxPriceLevelsPerSide];

        // Initialize free list.
        for (int i = 0; i < _nodes.Length - 1; i++)
        {
            _nodes[i].Next = i + 1;
        }

        _nodes[^1].Next = None;
        _freeHead = 0;

        _bidLevels = 0;
        _askLevels = 0;

        _nextInternalOrderId = 1;
    }

    public int BidLevels => _bidLevels;
    public int AskLevels => _askLevels;

    public bool TryGetBestBidPrice(out long price)
    {
        if (_bidLevels == 0)
        {
            price = 0;
            return false;
        }

        price = _bids[0].Price;
        return true;
    }

    public bool TryGetBestAskPrice(out long price)
    {
        if (_askLevels == 0)
        {
            price = 0;
            return false;
        }

        price = _asks[0].Price;
        return true;
    }

    public MatchResult Match(Order incoming)
    {
        if (incoming.Quantity <= 0 || incoming.Price <= 0)
        {
            return new MatchResult(false, RejectReason.Invalid, 0, incoming.Quantity, 0);
        }

        if (!_balances.TryLockFor(in incoming))
        {
            return new MatchResult(false, RejectReason.InsufficientFunds, 0, incoming.Quantity, 0);
        }

        long remaining = incoming.Quantity;
        long filled = 0;
        int trades = 0;

        if (incoming.Side == Side.Buy)
        {
            // Match against asks (best = lowest price).
            while (remaining > 0 && _askLevels > 0)
            {
                ref PriceLevel bestAsk = ref _asks[0];
                if (bestAsk.Price > incoming.Price) break;

                int nodeIndex = bestAsk.Head;
                while (remaining > 0 && nodeIndex != None)
                {
                    ref BookOrderNode maker = ref _nodes[nodeIndex];
                    long execPrice = bestAsk.Price; // maker price

                    long tradeQty = remaining <= maker.RemainingQuantity ? remaining : maker.RemainingQuantity;

                    // Buyer is taker; seller is maker.
                    _balances.SettleTrade(
                        buyerId: incoming.TraderId,
                        sellerId: maker.TraderId,
                        quantity: tradeQty,
                        executionPrice: execPrice,
                        buyerLimitPrice: incoming.Price
                    );

                    remaining -= tradeQty;
                    filled += tradeQty;
                    trades++;

                    maker.RemainingQuantity -= tradeQty;
                    bestAsk.TotalQuantity -= tradeQty;

                    if (maker.RemainingQuantity == 0)
                    {
                        int next = maker.Next;
                        FreeNode(nodeIndex);
                        nodeIndex = next;
                        bestAsk.Head = nodeIndex;
                        if (nodeIndex == None) bestAsk.Tail = None;
                    }
                    else
                    {
                        // Maker still resting.
                        break;
                    }
                }

                if (bestAsk.Head == None)
                {
                    RemoveAskLevelAt0();
                }
                else
                {
                    // Still orders at best ask.
                    break;
                }
            }

            if (remaining > 0)
            {
                if (!AddResting(incoming, remaining))
                {
                    // Roll back unfilled locked funds.
                    _balances.ReleaseUnfilled(in incoming, remaining);
                    return new MatchResult(false, RejectReason.CapacityExceeded, filled, remaining, trades);
                }

                return new MatchResult(true, RejectReason.None, filled, remaining, trades);
            }

            // Fully filled; should have consumed the entire lock via SettleTrade.
            return new MatchResult(true, RejectReason.None, filled, 0, trades);
        }
        else
        {
            // Match against bids (best = highest price).
            while (remaining > 0 && _bidLevels > 0)
            {
                ref PriceLevel bestBid = ref _bids[0];
                if (bestBid.Price < incoming.Price) break;

                int nodeIndex = bestBid.Head;
                while (remaining > 0 && nodeIndex != None)
                {
                    ref BookOrderNode maker = ref _nodes[nodeIndex];
                    long execPrice = bestBid.Price; // maker price

                    long tradeQty = remaining <= maker.RemainingQuantity ? remaining : maker.RemainingQuantity;

                    // Buyer is maker; seller is taker.
                    _balances.SettleTrade(
                        buyerId: maker.TraderId,
                        sellerId: incoming.TraderId,
                        quantity: tradeQty,
                        executionPrice: execPrice,
                        buyerLimitPrice: maker.Price
                    );

                    remaining -= tradeQty;
                    filled += tradeQty;
                    trades++;

                    maker.RemainingQuantity -= tradeQty;
                    bestBid.TotalQuantity -= tradeQty;

                    if (maker.RemainingQuantity == 0)
                    {
                        int next = maker.Next;
                        FreeNode(nodeIndex);
                        nodeIndex = next;
                        bestBid.Head = nodeIndex;
                        if (nodeIndex == None) bestBid.Tail = None;
                    }
                    else
                    {
                        break;
                    }
                }

                if (bestBid.Head == None)
                {
                    RemoveBidLevelAt0();
                }
                else
                {
                    break;
                }
            }

            if (remaining > 0)
            {
                if (!AddResting(incoming, remaining))
                {
                    _balances.ReleaseUnfilled(in incoming, remaining);
                    return new MatchResult(false, RejectReason.CapacityExceeded, filled, remaining, trades);
                }

                return new MatchResult(true, RejectReason.None, filled, remaining, trades);
            }

            return new MatchResult(true, RejectReason.None, filled, 0, trades);
        }
    }

    public void CancelAllOpenOrders()
    {
        // Release bids.
        for (int i = 0; i < _bidLevels; i++)
        {
            ref PriceLevel level = ref _bids[i];
            int idx = level.Head;
            while (idx != None)
            {
                ref BookOrderNode n = ref _nodes[idx];
                int next = n.Next;

                // Buy order releases locked quote: remaining * limit.
                long refund = checked(n.RemainingQuantity * n.Price);
                _balances.LockedQuote[n.TraderId] -= refund;
                _balances.AvailableQuote[n.TraderId] += refund;

                FreeNode(idx);
                idx = next;
            }
        }

        // Release asks.
        for (int i = 0; i < _askLevels; i++)
        {
            ref PriceLevel level = ref _asks[i];
            int idx = level.Head;
            while (idx != None)
            {
                ref BookOrderNode n = ref _nodes[idx];
                int next = n.Next;

                // Sell order releases locked base.
                long refund = n.RemainingQuantity;
                _balances.LockedBase[n.TraderId] -= refund;
                _balances.AvailableBase[n.TraderId] += refund;

                FreeNode(idx);
                idx = next;
            }
        }

        _bidLevels = 0;
        _askLevels = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool AddResting(Order incoming, long remainingQuantity)
    {
        int nodeIndex = AllocNode();
        if (nodeIndex == None) return false;

        ref BookOrderNode node = ref _nodes[nodeIndex];
        node.OrderId = incoming.OrderId != 0 ? incoming.OrderId : _nextInternalOrderId++;
        node.TraderId = incoming.TraderId;
        node.Price = incoming.Price;
        node.RemainingQuantity = remainingQuantity;
        node.Next = None;

        if (incoming.Side == Side.Buy)
        {
            int pos = FindBidLevel(incoming.Price, out bool found);
            if (found)
            {
                EnqueueAtLevel(ref _bids[pos], nodeIndex);
                _bids[pos].TotalQuantity += remainingQuantity;
                return true;
            }

            if (_bidLevels == _bids.Length)
            {
                FreeNode(nodeIndex);
                return false;
            }

            InsertBidLevel(pos, incoming.Price, nodeIndex, remainingQuantity);
            return true;
        }
        else
        {
            int pos = FindAskLevel(incoming.Price, out bool found);
            if (found)
            {
                EnqueueAtLevel(ref _asks[pos], nodeIndex);
                _asks[pos].TotalQuantity += remainingQuantity;
                return true;
            }

            if (_askLevels == _asks.Length)
            {
                FreeNode(nodeIndex);
                return false;
            }

            InsertAskLevel(pos, incoming.Price, nodeIndex, remainingQuantity);
            return true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnqueueAtLevel(ref PriceLevel level, int nodeIndex)
    {
        if (level.Head == None)
        {
            level.Head = nodeIndex;
            level.Tail = nodeIndex;
            return;
        }

        // Append to FIFO.
        int tail = level.Tail;
        _nodes[tail].Next = nodeIndex;
        level.Tail = nodeIndex;
    }

    private void InsertBidLevel(int pos, long price, int nodeIndex, long totalQty)
    {
        // Shift [pos..end) right by one.
        for (int i = _bidLevels; i > pos; i--)
        {
            _bids[i] = _bids[i - 1];
        }

        _bids[pos] = new PriceLevel(price, nodeIndex, nodeIndex, totalQty);
        _bidLevels++;
    }

    private void InsertAskLevel(int pos, long price, int nodeIndex, long totalQty)
    {
        for (int i = _askLevels; i > pos; i--)
        {
            _asks[i] = _asks[i - 1];
        }

        _asks[pos] = new PriceLevel(price, nodeIndex, nodeIndex, totalQty);
        _askLevels++;
    }

    private void RemoveBidLevelAt0()
    {
        // Shift left by one.
        for (int i = 1; i < _bidLevels; i++)
        {
            _bids[i - 1] = _bids[i];
        }

        _bidLevels--;
    }

    private void RemoveAskLevelAt0()
    {
        for (int i = 1; i < _askLevels; i++)
        {
            _asks[i - 1] = _asks[i];
        }

        _askLevels--;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int AllocNode()
    {
        int idx = _freeHead;
        if (idx == None) return None;
        _freeHead = _nodes[idx].Next;
        _nodes[idx].Next = None;
        return idx;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FreeNode(int idx)
    {
        _nodes[idx].OrderId = 0;
        _nodes[idx].TraderId = 0;
        _nodes[idx].Price = 0;
        _nodes[idx].RemainingQuantity = 0;
        _nodes[idx].Next = _freeHead;
        _freeHead = idx;
    }

    private int FindBidLevel(long price, out bool found)
    {
        // Bids are sorted descending by price.
        int lo = 0;
        int hi = _bidLevels - 1;

        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            long midPrice = _bids[mid].Price;

            if (midPrice == price)
            {
                found = true;
                return mid;
            }

            if (midPrice < price)
            {
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        found = false;
        return lo;
    }

    private int FindAskLevel(long price, out bool found)
    {
        // Asks are sorted ascending by price.
        int lo = 0;
        int hi = _askLevels - 1;

        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            long midPrice = _asks[mid].Price;

            if (midPrice == price)
            {
                found = true;
                return mid;
            }

            if (midPrice > price)
            {
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        found = false;
        return lo;
    }

    private struct PriceLevel
    {
        public PriceLevel(long price, int head, int tail, long totalQuantity)
        {
            Price = price;
            Head = head;
            Tail = tail;
            TotalQuantity = totalQuantity;
        }

        public long Price;
        public int Head;
        public int Tail;
        public long TotalQuantity;
    }

    private struct BookOrderNode
    {
        public long OrderId;
        public int TraderId;
        public long Price;
        public long RemainingQuantity;
        public int Next;
    }
}
