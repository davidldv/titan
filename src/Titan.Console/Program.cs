using System.Diagnostics;
using Titan.Engine;

const int traders = 32;
const int orders = 50_000;

var balances = new BalanceSheet(traders);
for (int i = 0; i < traders; i++)
{
	balances.AddAccount(initialBase: 1_000_000, initialQuote: 1_000_000_000_000);
}

var book = new OrderBook(balances, maxRestingOrders: 100_000, maxPriceLevelsPerSide: 2_000);
var rng = new Random(42);

var sw = Stopwatch.StartNew();
for (int i = 1; i <= orders; i++)
{
	var order = new Order(
		OrderId: i,
		TraderId: rng.Next(traders),
		Side: (rng.Next(2) == 0) ? Side.Buy : Side.Sell,
		Price: rng.Next(950, 1051),
		Quantity: rng.Next(1, 50)
	);

	var r = book.Match(order);
	if (!r.Accepted)
	{
		Console.WriteLine($"Rejected: {r.RejectReason}");
		break;
	}
}

book.CancelAllOpenOrders();
sw.Stop();

Console.WriteLine($"Processed {orders:n0} orders in {sw.ElapsedMilliseconds:n0} ms");
Console.WriteLine($"Top of book: bids={book.BidLevels} asks={book.AskLevels}");
