// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// BENCH ONLY (branch bench/handoff-reject-diag, not for merge): counts why the prewarm handoff rejects a footprint
/// (the first precondition that no longer holds) and logs a summary every <see cref="Window"/> blocks at INFO.
/// Only the rejection path records anything; a footprint that matches costs nothing extra.
/// </summary>
internal static class HandoffRejectDiag
{
    private const int Window = 50;
    private const int Top = 15;

    private static readonly ConcurrentDictionary<string, long> ByKind = new();
    private static readonly ConcurrentDictionary<Address, long> ByAddress = new();
    private static readonly ConcurrentDictionary<string, long> ByAddressKind = new();
    private static readonly ConcurrentDictionary<string, long> BySlot = new();
    private static Address? _coinbase;
    private static ulong _lastBlock = ulong.MaxValue;
    private static int _blocks;
    private static long _rejects;
    private static long _handoffs0, _rejected0, _missing0;

    /// <summary>Called by the main-processing adapter when a block starts; logs and resets every <see cref="Window"/> blocks.</summary>
    public static void OnBlock(BlockHeader header, ILogger logger)
    {
        if (header.Number == _lastBlock) return;
        _lastBlock = header.Number;
        _coinbase = header.Beneficiary;
        if (_blocks == 0)
        {
            _handoffs0 = Blockchain.Metrics.PrewarmHandoffs;
            _rejected0 = Blockchain.Metrics.PrewarmHandoffsRejected;
            _missing0 = Blockchain.Metrics.PrewarmHandoffsMissing;
        }

        if (++_blocks < Window) return;

        if (logger.IsInfo)
        {
            long taken = Blockchain.Metrics.PrewarmHandoffs - _handoffs0;
            long rejected = Blockchain.Metrics.PrewarmHandoffsRejected - _rejected0;
            long missing = Blockchain.Metrics.PrewarmHandoffsMissing - _missing0;
            StringBuilder sb = new();
            sb.Append($"HandoffDiag blocks={_blocks} to={header.Number - 1} taken={taken} rejected={rejected} missing={missing} recorded={_rejects} | kinds:");
            foreach ((string k, long v) in ByKind.OrderByDescending(static x => x.Value)) sb.Append($" {k}={v}");
            sb.Append(" | top addresses:");
            foreach ((Address a, long v) in ByAddress.OrderByDescending(static x => x.Value).Take(Top))
            {
                string kinds = string.Join('+', ByAddressKind.Where(x => x.Key.StartsWith(a.ToString(), System.StringComparison.Ordinal))
                    .OrderByDescending(static x => x.Value).Take(3).Select(static x => $"{x.Key[(x.Key.IndexOf('|') + 1)..]}:{x.Value}"));
                sb.Append($" {a}={v}({kinds})");
            }

            sb.Append(" | top slots:");
            foreach ((string s, long v) in BySlot.OrderByDescending(static x => x.Value).Take(Top)) sb.Append($" {s}={v}");
            logger.Info(sb.ToString());
        }

        ByKind.Clear();
        ByAddress.Clear();
        ByAddressKind.Clear();
        BySlot.Clear();
        _blocks = 0;
        _rejects = 0;
    }

    public static void Account(Transaction tx, Address address, string field) =>
        Record(address, $"acct:{field}:{Relation(tx, address)}", null);

    public static void Slot(Transaction tx, in StorageCell cell) =>
        Record(cell.Address, $"slot:{Relation(tx, cell.Address)}", $"{cell.Address}:{cell.Index}");

    private static string Relation(Transaction tx, Address address) =>
        address == tx.SenderAddress ? "sender"
        : address == tx.To ? "to"
        : address == _coinbase ? "coinbase"
        : "other";

    private static void Record(Address address, string kind, string? slot)
    {
        _rejects++;
        ByKind.AddOrUpdate(kind, 1, static (_, v) => v + 1);
        ByAddress.AddOrUpdate(address, 1, static (_, v) => v + 1);
        ByAddressKind.AddOrUpdate($"{address}|{kind}", 1, static (_, v) => v + 1);
        if (slot is not null) BySlot.AddOrUpdate(slot, 1, static (_, v) => v + 1);
    }
}
