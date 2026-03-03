using Xunit;

namespace Titan.Engine.Tests;

public sealed class OrderBookStressTests
{
    [Fact]
    public void Match_5kRandomOrders_TotalsMatchReferenceAfterEachStep()
    {
        const int traders = 64;
        const int orders = 5_000;
        const long initialBase = 1_000_000;
        const long initialQuote = 1_000_000_000_000;

        var balances = new Titan.Engine.BalanceSheet(traders);
        for (int i = 0; i < traders; i++)
        {
            balances.AddAccount(initialBase, initialQuote);
        }

        var orderBook = new Titan.Engine.OrderBook(
            balances,
            maxRestingOrders: 20_000,
            maxPriceLevelsPerSide: 1_000
        );

        var reference = new ReferenceMatcher(traders, initialBase, initialQuote);
        var rng = new Random(123_456);

        Titan.Engine.Order[] last = new Titan.Engine.Order[16];

        for (int i = 1; i <= orders; i++)
        {
            int traderId = rng.Next(traders);
            Titan.Engine.Side side = (rng.Next(2) == 0) ? Titan.Engine.Side.Buy : Titan.Engine.Side.Sell;
            long price = rng.Next(900, 1101);
            long qty = rng.Next(1, 101);

            var o = new Titan.Engine.Order(i, traderId, side, price, qty);
            last[i & 15] = o;

            var result = orderBook.Match(o);
            Assert.True(result.Accepted, $"Order rejected at i={i}: {result.RejectReason}");

            reference.Match(o);

            reference.TryGetBestBidAsk(out long refBid, out long refAsk);
            if (refBid != 0 && refAsk != 0)
            {
                Assert.True(refBid < refAsk, $"Reference book crossed at order={i}: bid={refBid} ask={refAsk}");
            }

            for (int t = 0; t < traders; t++)
            {
                long baseTotal = balances.AvailableBase[t] + balances.LockedBase[t];
                long quoteTotal = balances.AvailableQuote[t] + balances.LockedQuote[t];

                if (baseTotal != reference.Base[t] || quoteTotal != reference.Quote[t])
                {
                    orderBook.TryGetBestBidPrice(out long titanBestBid);
                    orderBook.TryGetBestAskPrice(out long titanBestAsk);
                    reference.TryGetBestBidAsk(out long refBestBid, out long refBestAsk);

                    string recent = string.Join(
                        "\n",
                        Enumerable.Range(0, 16)
                            .Select(k => last[(i - 15 + k) & 15])
                            .Where(ord => ord.OrderId != 0)
                            .Select(ord => $"#{ord.OrderId} t={ord.TraderId} {ord.Side} p={ord.Price} q={ord.Quantity}")
                    );

                    throw new Xunit.Sdk.XunitException(
                        $"Divergence at order={i} trader={t}: base expected {reference.Base[t]} actual {baseTotal}; quote expected {reference.Quote[t]} actual {quoteTotal}\n" +
                        $"Titan bestBid={titanBestBid} bestAsk={titanBestAsk}; Ref bestBid={refBestBid} bestAsk={refBestAsk}\n" +
                        "Recent orders:\n" + recent
                    );
                }
            }
        }
    }

    [Fact]
    public void Match_100kRandomOrders_BalancesMatchReferenceExactly()
    {
        const int traders = 512;
        const int orders = 100_000;

        // Use integers to avoid any floating point surprises.
        const long initialBase = 1_000_000;
        const long initialQuote = 1_000_000_000_000;

        var balances = new Titan.Engine.BalanceSheet(traders);
        for (int i = 0; i < traders; i++)
        {
            balances.AddAccount(initialBase, initialQuote);
        }

        var orderBook = new Titan.Engine.OrderBook(
            balances,
            maxRestingOrders: 200_000,
            maxPriceLevelsPerSide: 1_000
        );

        var reference = new ReferenceMatcher(traders, initialBase, initialQuote);

        var rng = new Random(123_456);

        for (int i = 1; i <= orders; i++)
        {
            int traderId = rng.Next(traders);
            Titan.Engine.Side side = (rng.Next(2) == 0) ? Titan.Engine.Side.Buy : Titan.Engine.Side.Sell;
            long price = rng.Next(900, 1101); // ticks
            long qty = rng.Next(1, 101); // lots

            var o = new Titan.Engine.Order(
                OrderId: i,
                TraderId: traderId,
                Side: side,
                Price: price,
                Quantity: qty
            );

            var result = orderBook.Match(o);
            Assert.True(result.Accepted, $"Order rejected: {result.RejectReason}");

            reference.Match(o);
        }

        // Cancel all open orders so Titan's balances reflect only executed trades.
        orderBook.CancelAllOpenOrders();

        for (int t = 0; t < traders; t++)
        {
            Assert.Equal(0, balances.LockedBase[t]);
            Assert.Equal(0, balances.LockedQuote[t]);

            long baseActual = balances.AvailableBase[t];
            long quoteActual = balances.AvailableQuote[t];
            long baseExpected = reference.Base[t];
            long quoteExpected = reference.Quote[t];

            if (baseActual != baseExpected || quoteActual != quoteExpected)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Mismatch trader={t}: base expected {baseExpected} actual {baseActual}; quote expected {quoteExpected} actual {quoteActual}"
                );
            }
        }

        Assert.Equal(traders * initialBase, Sum(balances.AvailableBase, traders));
        Assert.Equal(traders * initialQuote, Sum(balances.AvailableQuote, traders));
    }

    private static long Sum(long[] arr, int count)
    {
        long sum = 0;
        for (int i = 0; i < count; i++) sum += arr[i];
        return sum;
    }

    private sealed class ReferenceMatcher
    {
        private readonly SortedDictionary<long, Queue<RefOrder>> _bids;
        private readonly SortedDictionary<long, Queue<RefOrder>> _asks;

        public ReferenceMatcher(int traders, long initialBase, long initialQuote)
        {
            Base = new long[traders];
            Quote = new long[traders];

            for (int i = 0; i < traders; i++)
            {
                Base[i] = initialBase;
                Quote[i] = initialQuote;
            }

            _bids = new SortedDictionary<long, Queue<RefOrder>>(Comparer<long>.Create((a, b) => b.CompareTo(a)));
            _asks = new SortedDictionary<long, Queue<RefOrder>>();
        }

        public long[] Base { get; }
        public long[] Quote { get; }

        public void TryGetBestBidAsk(out long bestBid, out long bestAsk)
        {
            bestBid = 0;
            bestAsk = 0;

            foreach (var kv in _bids)
            {
                bestBid = kv.Key;
                break;
            }

            foreach (var kv in _asks)
            {
                bestAsk = kv.Key;
                break;
            }
        }

        public void Match(Titan.Engine.Order incoming)
        {
            long remaining = incoming.Quantity;

            if (incoming.Side == Titan.Engine.Side.Buy)
            {
                while (remaining > 0 && TryGetBestAsk(out long askPrice, out Queue<RefOrder> queue))
                {
                    if (askPrice > incoming.Price) break;

                    while (remaining > 0 && queue.Count > 0)
                    {
                        var maker = queue.Peek();
                        long tradeQty = remaining <= maker.Remaining ? remaining : maker.Remaining;

                        ExecuteTrade(
                            buyerId: incoming.TraderId,
                            sellerId: maker.TraderId,
                            quantity: tradeQty,
                            price: askPrice
                        );

                        remaining -= tradeQty;
                        maker.Remaining -= tradeQty;

                        if (maker.Remaining == 0)
                        {
                            queue.Dequeue();
                        }
                    }

                    if (queue.Count == 0)
                    {
                        _asks.Remove(askPrice);
                    }
                    else
                    {
                        break;
                    }
                }

                if (remaining > 0)
                {
                    Enqueue(_bids, incoming.Price, new RefOrder(incoming.TraderId, remaining));
                }
            }
            else
            {
                while (remaining > 0 && TryGetBestBid(out long bidPrice, out Queue<RefOrder> queue))
                {
                    if (bidPrice < incoming.Price) break;

                    while (remaining > 0 && queue.Count > 0)
                    {
                        var maker = queue.Peek();
                        long tradeQty = remaining <= maker.Remaining ? remaining : maker.Remaining;

                        ExecuteTrade(
                            buyerId: maker.TraderId,
                            sellerId: incoming.TraderId,
                            quantity: tradeQty,
                            price: bidPrice
                        );

                        remaining -= tradeQty;
                        maker.Remaining -= tradeQty;

                        if (maker.Remaining == 0)
                        {
                            queue.Dequeue();
                        }
                    }

                    if (queue.Count == 0)
                    {
                        _bids.Remove(bidPrice);
                    }
                    else
                    {
                        break;
                    }
                }

                if (remaining > 0)
                {
                    Enqueue(_asks, incoming.Price, new RefOrder(incoming.TraderId, remaining));
                }
            }
        }

        private void ExecuteTrade(int buyerId, int sellerId, long quantity, long price)
        {
            long notional = checked(quantity * price);

            Base[buyerId] += quantity;
            Quote[buyerId] -= notional;

            Base[sellerId] -= quantity;
            Quote[sellerId] += notional;
        }

        private static void Enqueue(SortedDictionary<long, Queue<RefOrder>> book, long price, RefOrder order)
        {
            if (!book.TryGetValue(price, out var q))
            {
                q = new Queue<RefOrder>();
                book.Add(price, q);
            }

            q.Enqueue(order);
        }

        private bool TryGetBestAsk(out long price, out Queue<RefOrder> queue)
        {
            foreach (var kv in _asks)
            {
                price = kv.Key;
                queue = kv.Value;
                return true;
            }

            price = 0;
            queue = null!;
            return false;
        }

        private bool TryGetBestBid(out long price, out Queue<RefOrder> queue)
        {
            foreach (var kv in _bids)
            {
                price = kv.Key;
                queue = kv.Value;
                return true;
            }

            price = 0;
            queue = null!;
            return false;
        }

        private sealed class RefOrder
        {
            public RefOrder(int traderId, long remaining)
            {
                TraderId = traderId;
                Remaining = remaining;
            }

            public int TraderId { get; }
            public long Remaining;
        }
    }
}
