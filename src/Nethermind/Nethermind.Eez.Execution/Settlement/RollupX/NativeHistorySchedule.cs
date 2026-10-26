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
        ReadOnlyMemory<byte> import = default;
        if (updating)
        {
            if (source.Number < state.Endpoint || source.Number - state.Endpoint > NativeHistoryCall.MaxHashes)
                throw NativeL1Header.Invalid("history import exceeds bound or regresses");
            if (cutoff > ulong.MaxValue - Interval) throw NativeL1Header.Invalid("next cutoff overflow");
            import = NativeHistoryCall.Encode(cutoff, chain, state.Endpoint, source.Number);
        }
        return new NativeHistoryExecution(cutoff, source.Number, source.Hash, source.PrevRandao, import);
    }
}

/// <summary>History values read from the authenticated parent state, rather than an RPC assertion.</summary>
internal readonly record struct NativeHistoryState(ulong FirstNumber, ulong Endpoint, ValueHash256 EndpointHash, ulong NextCutoff);

/// <summary>Recorded origin and pre-execution import; final anchor authorization remains required.</summary>
internal readonly record struct NativeHistoryExecution(ulong Cutoff, ulong OriginNumber, ValueHash256 OriginHash,
    ValueHash256 PrevRandao, ReadOnlyMemory<byte> Import);
