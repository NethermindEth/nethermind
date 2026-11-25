// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

internal readonly record struct ValidatedRollupXBlock(
    ulong Number,
    ValueHash256 Hash,
    ValueHash256 ParentHash,
    ValueHash256 PreStateRoot,
    ValueHash256 PostStateRoot,
    ulong ParentNumber,
    ulong ParentTimestamp,
    Address? Beneficiary,
    ReadOnlyMemory<byte> ExtraData,
    ReadOnlyMemory<byte> Rlp,
    RollupXOperationsCheck.IntermediateFailure Failure,
    int L1CheckpointLength,
    ValueHash256 L1CheckpointHash)
{
    internal static ValidatedRollupXBlock Capture(EezStatelessBlockResult result, ReadOnlyMemory<byte> canonicalRlp,
        EezChainRules rules, int l1CheckpointLength, ValueHash256 l1CheckpointHash) =>
        new(result.Block.Number, result.Hash.ValueHash256, result.Parent.Hash!.ValueHash256, result.PreStateRoot.ValueHash256,
            result.PostStateRoot.ValueHash256, result.Parent.Number, result.Parent.Timestamp, result.Block.Header.Beneficiary, result.Block.Header.ExtraData,
            canonicalRlp, RollupXOperationsCheck.CheckIntermediate(result, rules), l1CheckpointLength, l1CheckpointHash);

    internal bool MatchesTransactions(ReadOnlySpan<ReadOnlyMemory<byte>> transactions, CancellationToken cancellation)
    {
        RlpReader reader = new(Rlp.Span);
        RlpReader fields = reader.EnterSequence(out _);
        fields.SkipItem();
        RlpReader body = fields.EnterSequence(out _);
        foreach (ReadOnlyMemory<byte> expected in transactions)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!body.HasNext) return false;
            // Block RLP wraps the typed envelope as a byte string. A legacy transaction is already a list.
            ReadOnlySpan<byte> encoded = body.IsNextSequence ? body.ReadEncodedItem() : body.ReadBytes();
            if (!encoded.SequenceEqual(expected.Span)) return false;
        }
        return !body.HasNext;
    }
}
