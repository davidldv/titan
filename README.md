# Titan

A high-throughput limit order book matching engine in C# / .NET 10, with event-sourced
persistence on PostgreSQL and a real-time web UI driven by SignalR.

Titan is designed around the realities of an electronic exchange: every incoming order
must be matched in deterministic price-time priority, settled against a live balance
sheet, and persisted to an append-only ledger so the entire book can be rebuilt by
replay. The implementation favours allocation-light data structures so the hot path
stays predictable under load.

## Highlights

- **Price-time priority matching** with sorted price levels and FIFO order queues.
- **Allocation-light hot path** — orders, levels, and resting nodes are stored in
  pre-sized arrays with an intrusive free list; matching does not allocate per order.
- **Pre-trade risk** via a `BalanceSheet` that locks base/quote on order entry and
  settles atomically on each fill, with rollback for partial rejects.
- **Event sourcing** — every `OrderPlaced`, `OrderRejected`, and `TradeExecuted` event
  is appended through Marten to a per-symbol PostgreSQL stream, enabling audit and
  full state replay.
- **Real-time UI** — ASP.NET Core minimal API exposes order entry; SignalR broadcasts
  top-of-book snapshots to connected clients on every match.
- **Tested under stress** — an xUnit harness drives 100k randomised orders through the
  engine and asserts that balances and fills match a naive reference matcher exactly,
  catching any drift introduced by the optimised data structures.

## Architecture

```
                  ┌──────────────┐
   POST /api/orders ─►│ Web (Kestrel)│──►Channel<Order>──►┐
                  │ SignalR Hub  │◄──snapshots──────┐ │
                  └──────────────┘                  │ │
                                                    │ ▼
                                          ┌─────────┴───────────┐
                                          │  EngineLoop         │
                                          │  (BackgroundService)│
                                          └─────────┬───────────┘
                                                    │
                              ┌─────────────────────┼─────────────────────┐
                              ▼                     ▼                     ▼
                       ┌─────────────┐       ┌─────────────┐       ┌──────────────┐
                       │  OrderBook  │──────►│ BalanceSheet│       │   Ledger     │
                       │  (matching) │       │   (risk)    │       │ (Marten/PG)  │
                       └─────────────┘       └─────────────┘       └──────────────┘
```

| Project              | Responsibility                                                                   |
| -------------------- | -------------------------------------------------------------------------------- |
| `Titan.Engine`       | Pure matching engine, balance sheet, snapshot DTOs.                              |
| `Titan.Persistence`  | Marten-based event store (`OrderPlaced`, `OrderRejected`, `TradeExecuted`).      |
| `Titan.Web`          | Minimal API, SignalR broadcaster, hosted `EngineLoop`, dev Postgres bootstrap.   |
| `Titan.Console`      | Headless harness that pumps orders through the engine.                           |
| `Titan.Engine.Tests` | xUnit unit + 100k-order stress test against a reference matcher.                 |
| `Titan.Persistence.Tests` | Integration tests for the Marten event stream against a real Postgres.      |

## Engine internals

The order book stores price levels in two sorted arrays (`_bids` descending,
`_asks` ascending) and uses binary search to locate an insertion point. Each level
holds a doubly-linked FIFO of resting orders backed by a single `BookOrderNode[]`
pool with an intrusive free list (`_freeHead`). Allocating a resting order is an
`O(1)` pop from the free list; cancelling returns the node to the head. The matching
loop walks the best level, settles each fill against the `BalanceSheet`, and
optionally records each fill into a caller-supplied `List<Trade>` so the hosting
application can persist per-fill events without the engine itself having to know
about persistence.

`BalanceSheet` keeps four parallel `long[]` arrays (`AvailableBase`, `LockedBase`,
`AvailableQuote`, `LockedQuote`) indexed by trader id. Locking and settlement are
straightforward integer arithmetic; partial-fill / capacity-exceeded paths roll the
locked amount back to available so balances are never silently lost.

## Event sourcing

`Ledger.AppendEventsAsync` opens a Marten lightweight session and appends a batch of
`OrderBookEvent`s to a stream keyed per matching engine instance (today a single
constant id; in production this would be partitioned per symbol/ticker). Because the
stream is append-only and every input order produces at least one event, the entire
order book and balance state can be reconstructed by replaying the stream — useful
for audit, recovery, and analytics.

## Running locally

Requirements:

- .NET 10 SDK
- Docker (the web host boots a PostgreSQL 15 container via Testcontainers; no manual
  database setup required)

```bash
dotnet run --project src/Titan.Web
```

Then open <http://localhost:5000> for the live order-book UI, or POST to the API:

```bash
curl -X POST http://localhost:5000/api/orders \
  -H 'Content-Type: application/json' \
  -d '{"traderId":1,"side":"Buy","price":10000,"quantity":5}'
```

## Tests

```bash
# Unit + stress (no infrastructure required)
dotnet test test/Titan.Engine.Tests

# Persistence integration (spins up Postgres via Testcontainers)
dotnet test test/Titan.Persistence.Tests
```

The stress suite drives 5k and 100k randomised orders through the engine and asserts
that filled quantities, locked balances, and settled balances exactly match a naive
reference matcher after every step, giving high confidence that the optimised
engine is behaviourally identical to the obvious implementation.

## Tech stack

- **C# 13 / .NET 10** — primary language and runtime.
- **ASP.NET Core minimal APIs + SignalR** — HTTP entry point and real-time push.
- **System.Threading.Channels** — single-reader queue between the request thread and
  the matching loop, so the engine stays single-threaded and lock-free.
- **Marten** — event sourcing on PostgreSQL.
- **Testcontainers for .NET** — disposable Postgres for both dev runs and integration
  tests.
- **xUnit** — unit and stress tests.

## Status & roadmap

Working today: limit-order matching, pre-trade risk, snapshot broadcast, event
append, integration tests. Natural next steps:

- Replay path that rebuilds the book and balance sheet from the ledger on startup.
- Per-symbol stream partitioning and a real connection string for production
  deployments (the Testcontainers bootstrap is a dev convenience only).
- IOC, FOK, post-only, and cancel/replace order types.
- Projections (trade tape, OHLC) built off the event stream.
