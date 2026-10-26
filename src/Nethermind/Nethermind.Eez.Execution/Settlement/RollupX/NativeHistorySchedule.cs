// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>The delayed L1 history cutoff selected at each aligned L2 timestamp.</summary>
internal sealed class NativeHistorySchedule
{
    internal ulong FirstCutoff { get; }
    internal ulong BlockInterval { get; }
    internal ulong Interval { get; }
    internal ulong Delay { get; }
    internal ulong BootstrapCutoff => FirstCutoff - Interval;

    internal NativeHistorySchedule(ulong firstCutoff, ulong blockInterval, uint positions, uint offset)
    {
        if (blockInterval == 0 || positions < 2 || offset >= positions || blockInterval > ulong.MaxValue / positions)
            throw new ArgumentException("Invalid history update schedule.");
        ulong interval = blockInterval * positions;
        ulong delay = blockInterval * ((ulong)offset + 1);
        if (firstCutoff < interval || firstCutoff > ulong.MaxValue - delay)
            throw new ArgumentException("History bootstrap or first update exceeds the timestamp range.");
        FirstCutoff = firstCutoff;
        BlockInterval = blockInterval;
        Interval = interval;
        Delay = delay;
    }

    internal ulong SourceCutoff(ulong timestamp)
    {
        if (timestamp < FirstCutoff || (timestamp - FirstCutoff) % BlockInterval != 0)
            throw new ArgumentException("L2 timestamp is outside the history schedule.");
        ulong elapsed = timestamp - FirstCutoff;
        return elapsed < Delay ? BootstrapCutoff : FirstCutoff + ((elapsed - Delay) / Interval) * Interval;
    }

    internal NativeHistoryExecution Resolve(ulong timestamp, in NativeHistoryState state, NativeL1HeaderChain chain, ulong? selected)
    {
        NativeHistoryExecution execution = ResolveOrigin(timestamp, in state, chain, selected);
        return selected.HasValue
            ? execution with { Import = NativeHistoryCall.Encode(execution.Cutoff, chain, state.Endpoint, execution.OriginNumber) }
            : execution;
    }

    internal NativeHistoryExecution VerifyRecorded(ulong timestamp, in NativeHistoryState state, NativeL1HeaderChain chain, ReadOnlyMemory<byte>? recorded)
    {
        ulong? selected = null;
        ulong cutoff = 0;
        ReadOnlySpan<byte> hashes = default;
        if (recorded is { } input)
        {
            hashes = NativeHistoryCall.Decode(input.Span, out cutoff);
            ulong count = (ulong)(hashes.Length / AbiWord.Size);
            if (state.Endpoint > ulong.MaxValue - count) throw NativeL1Header.Invalid("history endpoint overflow");
            selected = state.Endpoint + count;
        }
        NativeHistoryExecution execution = ResolveOrigin(timestamp, in state, chain, selected);
        if (recorded is null) return execution;
        if (cutoff != execution.Cutoff) throw NativeL1Header.Invalid("recorded history cutoff mismatch");
        for (int offset = 0; offset < hashes.Length / AbiWord.Size; offset++)
        {
            ValueHash256 recordedHash = new(hashes.Slice(offset * AbiWord.Size, AbiWord.Size));
            if (recordedHash != chain.At(state.Endpoint + (ulong)offset + 1).Hash)
                throw NativeL1Header.Invalid("recorded history differs from checked L1 headers");
        }
        return execution with { Import = recorded.Value };
    }

    private NativeHistoryExecution ResolveOrigin(ulong timestamp, in NativeHistoryState state, NativeL1HeaderChain chain, ulong? selected)
    {
        if (timestamp <= FirstCutoff || (timestamp - FirstCutoff) % BlockInterval != 0)
            throw NativeL1Header.Invalid("history requires an aligned post-genesis timestamp");
        ulong cutoff = SourceCutoff(timestamp);
        bool updating = timestamp - cutoff == Delay;
        if (!updating && cutoff > ulong.MaxValue - Interval) throw NativeL1Header.Invalid("next cutoff overflow");
        ulong expectedNext = updating ? cutoff : cutoff + Interval;
        if (state.FirstNumber > state.Endpoint || state.NextCutoff != expectedNext || chain.At(state.Endpoint).Hash != state.EndpointHash)
            throw NativeL1Header.Invalid("parent history state mismatch");
        if (updating != selected.HasValue) throw NativeL1Header.Invalid("missing or off-schedule history transaction");
        NativeL1Header source = chain.At(selected ?? state.Endpoint);
        if (source.Timestamp > cutoff) throw NativeL1Header.Invalid("origin is newer than delayed cutoff");
        if (updating)
        {
            if (source.Number < state.Endpoint || source.Number - state.Endpoint > NativeHistoryCall.MaxHashes)
                throw NativeL1Header.Invalid("history import exceeds bound or regresses");
            if (cutoff > ulong.MaxValue - Interval) throw NativeL1Header.Invalid("next cutoff overflow");
        }
        return new NativeHistoryExecution(cutoff, source.Number, source.Hash, source.PrevRandao, default);
    }
}

/// <summary>History values read from the authenticated parent state, rather than an RPC assertion.</summary>
internal readonly record struct NativeHistoryState(ulong FirstNumber, ulong Endpoint, ValueHash256 EndpointHash, ulong NextCutoff);

/// <summary>Recorded origin and pre-execution import; final anchor authorization remains required.</summary>
internal readonly record struct NativeHistoryExecution(ulong Cutoff, ulong OriginNumber, ValueHash256 OriginHash,
    ValueHash256 PrevRandao, ReadOnlyMemory<byte> Import);
