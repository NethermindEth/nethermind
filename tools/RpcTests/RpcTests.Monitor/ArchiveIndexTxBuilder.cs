// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Nethermind.RpcTests.Monitor;

/// <summary>
/// Drives the archive-index read path end to end: sources the accounts and storage slots touched in a
/// historical block, then replays that same read set as an <c>eth_call</c> at a ladder of increasing offsets
/// behind it (see <see cref="QueryOffsets"/>) so the floor seeks resolve at varying depth rather than only
/// direct tail hits.
/// </summary>
/// <remarks>
/// Each call touches every sourced account (<c>BALANCE</c> → one account-record archive lookup) and every
/// sourced storage slot (<c>SLOAD</c> → one storage archive lookup). Storage can only be read from within a
/// contract's own execution context (there is no <c>EXTSLOAD</c>), so each contract's <c>code</c> is replaced
/// via <c>stateOverride</c> with a slot-reader and the driver <c>STATICCALL</c>s into it. A <c>stateOverride</c>
/// swaps code only, leaving historical storage intact, so the reads still resolve through the archive index.
///
/// The driver bytecode is injected as runtime code at a synthetic address (not run as init-code), so it is
/// subject to neither the EIP-3860 init-code cap nor the EIP-170 deployed-code cap and scales to a full block.
/// </remarks>
internal sealed class ArchiveIndexTxBuilder(RpcClient client)
{
    // How far behind head to probe when no block is given: deep enough that reads resolve through the
    // historical store rather than the live (recent, unpersisted) flat tip.
    private const long SafetyOffset = 128;

    // Upper bounds on the touched set folded into one eth_call, sized for a public RPC:
    // - gas: worst case ~= (MaxAccounts + contracts) * 2600 (cold account) + MaxSlots * 2105 (cold SLOAD),
    //   which stays well under the ~30-50M eth_call gas cap public endpoints allow;
    // - body: state-override code is ~35 B/slot + ~23 B/account, so these caps keep the request under ~1 MB.
    // Raise both (and GasLimit) when probing a dedicated node with a higher --JsonRpc.GasCap.
    private const int MaxAccounts = 1000;
    private const int MaxSlots = 6000;

    // Requested gas; capped down by the node to its --JsonRpc.GasCap. 30M fits public caps and covers the
    // capped workload above (~16M typical, contracts sharing many slots).
    private const long GasLimit = 30_000_000;

    // Query-depth ladder: the same touched set is replayed at each offset behind the source block, so the
    // floor seek ("value at-or-before block") lands on progressively older, colder index entries instead of
    // only direct tail hits. Items created after (source - offset) resolve to not-found, exercising the
    // empty-seek path (option a: source once, query old — see benchmark notes).
    private static readonly long[] QueryOffsets = [0, 16, 256, 4_096, 65_536, 1_048_576];

    private const string CallerAddress = "0x0000000000000000000000000000000000000001";
    private const string DriverAddress = "0x0000000000000000000000000000000000c0ffee";
    private const string FallbackDriverAddress = "0x000000000000000000000000000000000000dead";

    // EVM opcodes
    private const byte OpStop = 0x00;
    private const byte OpBalance = 0x31;
    private const byte OpPop = 0x50;
    private const byte OpSload = 0x54;
    private const byte OpGas = 0x5A;
    private const byte OpPush1 = 0x60;
    private const byte OpPush20 = 0x73;
    private const byte OpPush32 = 0x7F;
    private const byte OpStaticCall = 0xFA;

    public async Task<IReadOnlyList<ArchiveIndexProbeResult>> RunAsync(long? block = null, CancellationToken ct = default)
    {
        IReadOnlyList<ArchiveIndexRequest> requests = await BuildRequestsAsync(block, ct);

        List<ArchiveIndexProbeResult> results = new(requests.Count);
        foreach (ArchiveIndexRequest request in requests)
            results.Add(await ProbeAsync(request, ct));

        return results;
    }

    /// <summary>
    /// Sources the touched set for <paramref name="block"/> (default: <see cref="SafetyOffset"/> behind head),
    /// caps/shuffles it, and builds one <c>eth_call</c> request per ladder rung — without sending any of them.
    /// </summary>
    public async Task<IReadOnlyList<ArchiveIndexRequest>> BuildRequestsAsync(long? block = null, CancellationToken ct = default)
    {
        long sourceBlock = block ?? await ResolveBlockAsync(ct);
        Sweep sourced = await SourceSweepAsync(sourceBlock, ct);
        // shuffle (avoids list-order locality) and cap to RPC limits once; the same set is replayed across the
        // ladder, seeded per source block for reproducibility.
        Sweep sweep = CapAndShuffle(sourced, new Random((int)sourceBlock));

        long[] queryBlocks = [.. QueryOffsets
            .Select(offset => Math.Max(0, sourceBlock - offset))
            .Distinct()]; // clamped rungs can collapse to the same block near genesis

        return [.. queryBlocks.Select(queryBlock => new ArchiveIndexRequest(
            queryBlock, sourceBlock - queryBlock, sweep.Accounts.Count, sweep.SlotCount, sweep.Source, BuildCallRequest(queryBlock, sweep)))];
    }

    private async Task<ArchiveIndexProbeResult> ProbeAsync(ArchiveIndexRequest request, CancellationToken ct)
    {
        long start = Stopwatch.GetTimestamp();
        JsonNode response = await client.RetrySendAsync(request.Request, ct);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

        return new ArchiveIndexProbeResult(request.QueryBlock, request.Offset, request.AccountCount, request.SlotCount, request.Source, elapsed, response);
    }

    private async Task<long> ResolveBlockAsync(CancellationToken ct)
    {
        JsonNode response = await client.RetrySendAsync(Rpc("eth_blockNumber"), ct);
        if (response["error"] is { } error)
            throw new Exception($"Failed to resolve head: {error.ToCompactString()}");

        long head = Convert.ToInt64(response["result"]!.GetValue<string>(), 16);
        return Math.Max(0, head - SafetyOffset);
    }

    #region Sourcing

    /// <summary>
    /// Collects the touched accounts/slots for a block: prestate tracer first, per-tx access lists as a fallback
    /// when the debug namespace is unavailable.
    /// </summary>
    private async Task<Sweep> SourceSweepAsync(long block, CancellationToken ct)
    {
        SweepBuilder sweep = new();

        // JsonObject traceRequest = Rpc("debug_traceBlockByNumber", Hex(block), new JsonObject { ["tracer"] = "prestateTracer" });
        // JsonNode traceResponse = await client.RetrySendAsync(traceRequest, ct);
        //
        // if (traceResponse["error"] is null && traceResponse["result"] is JsonArray txTraces)
        // {
        //     foreach (JsonNode? txTrace in txTraces)
        //     {
        //         if (txTrace?["result"] is JsonObject prestate)
        //             MergePrestate(sweep, prestate);
        //     }
        //
        //     return sweep.Build("prestate");
        // }
        //
        // Console.Error.WriteLine($"prestateTracer unavailable ({traceResponse["error"]?.ToCompactString() ?? "no result"}); " +
        //                         "falling back to eth_createAccessList");
        await SourceViaAccessListAsync(block, sweep, ct);
        return sweep.Build("accessList");
    }

    private static void MergePrestate(SweepBuilder sweep, JsonObject prestate)
    {
        foreach ((string address, JsonNode? state) in prestate)
        {
            sweep.AddAccount(address);
            if (state?["storage"] is JsonObject storage)
                foreach ((string slot, JsonNode? _) in storage)
                    sweep.AddSlot(address, slot);
        }
    }

    private async Task SourceViaAccessListAsync(long block, SweepBuilder sweep, CancellationToken ct)
    {
        JsonNode blockResponse = await client.RetrySendAsync(Rpc("eth_getBlockByNumber", Hex(block), true), ct);
        if (blockResponse["result"]?["transactions"] is not JsonArray transactions)
            throw new Exception($"Cannot source block #{block}: {blockResponse["error"]?.ToCompactString() ?? "no transactions"}");

        int failures = 0;
        string? firstError = null;
        foreach (JsonNode? tx in transactions)
            if (tx is JsonObject txObj && await MergeAccessListAsync(block, txObj, sweep, ct) is { } error)
            {
                failures++;
                firstError ??= error;
            }

        Console.Error.WriteLine($"accessList sourcing block #{block}: {transactions.Count} txs, {failures} eth_createAccessList failure(s)" +
                                (firstError is null ? "" : $"; first error: {firstError}"));
    }

    /// <summary>Adds a tx's participants and its access-list addresses/slots to the sweep; returns the RPC error
    /// message if <c>eth_createAccessList</c> failed (participants are still added), otherwise null.</summary>
    private async Task<string?> MergeAccessListAsync(long block, JsonObject tx, SweepBuilder sweep, CancellationToken ct)
    {
        // participants are touched regardless of whether the node supports eth_createAccessList
        if (Str(tx["from"]) is { } from) sweep.AddAccount(from);
        if (Str(tx["to"]) is { } toAddr) sweep.AddAccount(toAddr);

        JsonObject call = new() { ["from"] = Str(tx["from"]), ["gas"] = Str(tx["gas"]), ["value"] = Str(tx["value"]), ["data"] = Str(tx["input"]) };
        if (Str(tx["to"]) is { } to) call["to"] = to;

        // Simulate against the parent (pre-execution) state: at end-of-N the senders have already spent funds,
        // so replaying the same txs at N fails the balance check.
        JsonNode response = await client.RetrySendAsync(Rpc("eth_createAccessList", call, Hex(Math.Max(0, block - 1))), ct);
        if (response["error"] is { } error) return error.ToCompactString();

        if (response["result"]?["accessList"] is not JsonArray accessList) return null;
        foreach (JsonNode? entry in accessList)
        {
            if (Str(entry?["address"]) is not { } address) continue;
            sweep.AddAccount(address);
            if (entry!["storageKeys"] is JsonArray keys)
                foreach (JsonNode? key in keys)
                    if (Str(key) is { } slot)
                        sweep.AddSlot(address, slot);
        }
        return null;
    }

    #endregion

    #region Request assembly

    /// <summary>Shuffles the touched set (removing list-order locality) and caps it to the RPC limits.</summary>
    private static Sweep CapAndShuffle(Sweep sweep, Random rng)
    {
        IReadOnlyList<byte[]> accounts = Shuffled(sweep.Accounts, rng);
        IReadOnlyList<Contract> contracts = Shuffled(sweep.Contracts, rng);

        IReadOnlyList<byte[]> cappedAccounts = accounts.Count > MaxAccounts ? [.. accounts.Take(MaxAccounts)] : accounts;

        List<Contract> cappedContracts = new(contracts.Count);
        int budget = MaxSlots;
        foreach (Contract contract in contracts)
        {
            if (budget <= 0) break;
            cappedContracts.Add(contract.Slots.Count <= budget ? contract : contract with { Slots = [.. contract.Slots.Take(budget)] });
            budget -= contract.Slots.Count;
        }

        int keptSlots = MaxSlots - Math.Max(0, budget);
        if (accounts.Count > MaxAccounts || sweep.SlotCount > keptSlots)
            Console.Error.WriteLine($"Capped touched set to RPC limits: accounts {accounts.Count}->{cappedAccounts.Count}, slots {sweep.SlotCount}->{keptSlots}");

        return sweep with { Accounts = cappedAccounts, Contracts = cappedContracts };
    }

    private static JsonObject BuildCallRequest(long block, Sweep sweep)
    {
        string driverAddress = sweep.ContainsAccount(DriverAddress) ? FallbackDriverAddress : DriverAddress;

        JsonObject overrides = new() { [driverAddress] = new JsonObject { ["code"] = BuildDriverCode(sweep) } };
        foreach (Contract contract in sweep.Contracts)
            overrides[contract.HexAddress] = new JsonObject { ["code"] = BuildSlotReaderCode(contract.Slots) };

        JsonObject call = new()
        {
            ["from"] = CallerAddress,
            ["to"] = driverAddress,
            ["gas"] = Hex(GasLimit),
            ["data"] = "0x"
        };

        return Rpc("eth_call", call, Hex(block), overrides);
    }

    /// <summary>
    /// Driver runtime code: BALANCE every account (account-record lookup) and STATICCALL every contract that
    /// has slots (its overridden code performs the storage lookups).
    /// </summary>
    private static string BuildDriverCode(Sweep sweep)
    {
        List<byte> code = new(sweep.Accounts.Count * 23 + sweep.Contracts.Count * 30 + 1);

        foreach (byte[] account in sweep.Accounts)
        {
            code.Add(OpPush20); code.AddRange(account);
            code.Add(OpBalance); code.Add(OpPop);
        }

        foreach (Contract contract in sweep.Contracts)
        {
            // STATICCALL(gas, to, argsOffset=0, argsLength=0, retOffset=0, retLength=0)
            code.Add(OpPush1); code.Add(0x00); // retLength
            code.Add(OpPush1); code.Add(0x00); // retOffset
            code.Add(OpPush1); code.Add(0x00); // argsLength
            code.Add(OpPush1); code.Add(0x00); // argsOffset
            code.Add(OpPush20); code.AddRange(contract.Address); // to
            code.Add(OpGas); // forward all remaining gas
            code.Add(OpStaticCall); code.Add(OpPop);
        }

        code.Add(OpStop);
        return ToHex(code);
    }

    /// <summary>Per-contract override code: SLOAD each of the contract's slots, then STOP.</summary>
    private static string BuildSlotReaderCode(IReadOnlyList<byte[]> slots)
    {
        List<byte> code = new(slots.Count * 35 + 1);
        foreach (byte[] slot in slots)
        {
            code.Add(OpPush32); code.AddRange(slot);
            code.Add(OpSload); code.Add(OpPop);
        }
        code.Add(OpStop);
        return ToHex(code);
    }

    #endregion

    #region Helpers

    private static JsonObject Rpc(string method, params JsonNode?[] parameters) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = 1,
        ["method"] = method,
        ["params"] = new JsonArray(parameters)
    };

    private static IReadOnlyList<T> Shuffled<T>(IReadOnlyList<T> source, Random rng)
    {
        T[] copy = [.. source];
        for (int i = copy.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }
        return copy;
    }

    private static string Hex(long n) => $"0x{n:x}";
    private static string? Str(JsonNode? node) => node?.GetValue<string>();
    private static string ToHex(List<byte> code) => "0x" + Convert.ToHexString([.. code]).ToLowerInvariant();

    #endregion
}

internal sealed record Contract(byte[] Address, IReadOnlyList<byte[]> Slots)
{
    public string HexAddress { get; } = "0x" + Convert.ToHexString(Address).ToLowerInvariant();
}

/// <summary>Deduplicated, normalized touched set for a block; addresses/slots kept as raw bytes for bytecode emission.</summary>
internal sealed record Sweep(IReadOnlyList<byte[]> Accounts, IReadOnlyList<Contract> Contracts, string Source)
{
    public int SlotCount => Contracts.Sum(c => c.Slots.Count);

    public bool ContainsAccount(string address)
    {
        byte[] target = SweepBuilder.Parse(address, 20);
        return Accounts.Any(a => a.AsSpan().SequenceEqual(target));
    }
}

/// <summary>Accumulates and deduplicates touched addresses/slots while sourcing, keyed by normalized address hex.</summary>
internal sealed class SweepBuilder
{
    private readonly HashSet<string> _accounts = [];
    private readonly Dictionary<string, HashSet<string>> _storage = [];

    public void AddAccount(string address) => _accounts.Add(Normalize(address, 20));

    public void AddSlot(string address, string slot)
    {
        string account = Normalize(address, 20);
        _accounts.Add(account);
        if (!_storage.TryGetValue(account, out HashSet<string>? slots))
            _storage[account] = slots = [];
        slots.Add(Normalize(slot, 32));
    }

    public Sweep Build(string source)
    {
        byte[][] accounts = [.. _accounts.Select(FromHex)];
        Contract[] contracts = [.. _storage.Select(kv => new Contract(FromHex(kv.Key), [.. kv.Value.Select(FromHex)]))];
        return new Sweep(accounts, contracts, source);
    }

    public static byte[] Parse(string hex, int length)
    {
        ReadOnlySpan<char> span = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex.AsSpan(2) : hex.AsSpan();
        byte[] parsed = Convert.FromHexString(span);
        if (parsed.Length == length) return parsed;

        byte[] result = new byte[length];
        parsed.CopyTo(result, length - parsed.Length);
        return result;
    }

    private static string Normalize(string hex, int length) => "0x" + Convert.ToHexString(Parse(hex, length)).ToLowerInvariant();
    private static byte[] FromHex(string normalized) => Convert.FromHexString(normalized.AsSpan(2));
}

/// <summary>One ladder rung's prepared but unsent request: an <c>eth_call</c> replaying the touched set at
/// <paramref name="QueryBlock"/> (<paramref name="Offset"/> blocks behind the source block).</summary>
internal sealed record ArchiveIndexRequest(long QueryBlock, long Offset, int AccountCount, int SlotCount, string Source, JsonObject Request);

/// <summary>Result of one ladder rung: the read set replayed at <paramref name="QueryBlock"/> (which is
/// <paramref name="Offset"/> blocks behind the source block).</summary>
internal sealed record ArchiveIndexProbeResult(long QueryBlock, long Offset, int AccountCount, int SlotCount, string Source, TimeSpan Elapsed, JsonNode Response)
{
    public bool Failed => Response["error"] is not null;
}
