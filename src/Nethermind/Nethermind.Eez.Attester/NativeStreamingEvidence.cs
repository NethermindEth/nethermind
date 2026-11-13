// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Eez.Prove;
using Nethermind.Logging;

namespace Nethermind.Eez.Attester;

/// <summary>An immutable checked L1 snapshot. Staging never changes an acknowledged or in-flight prefix.</summary>
internal sealed class NativeStreamingEvidence
{
    private readonly NativeL1HeaderChain _headers;
    private readonly int _limit;
    internal int Count => _headers.Count;

    internal NativeStreamingEvidence(long maxItems)
    {
        if (maxItems <= 0) throw new ArgumentOutOfRangeException(nameof(maxItems));
        _limit = (int)Math.Min(maxItems, NativeL1HeaderChain.MaxHeaders);
        _headers = new NativeL1HeaderChain(_limit);
    }

    private NativeStreamingEvidence(NativeL1HeaderChain headers, int limit) => (_headers, _limit) = (headers, limit);

    internal NativeStreamingEvidence Stage(int prefixLength, ExecutionWitness witness, in NativeL1HeaderRules rules)
    {
        int count = witness.L1HeadersRlp.Count;
        if (prefixLength < 0 || prefixLength > Count || count > _limit - prefixLength)
            throw NativeL1Header.Invalid("session L1 checkpoint or count limit");
        if (prefixLength == Count && count == 0) return this;
        NativeL1HeaderChain staged = _headers.Prefix(prefixLength);
        if (count != 0)
        {
            using ArrayPoolList<ReadOnlyMemory<byte>> segment = new(count);
            for (int index = 0; index < count; index++) segment.Add(witness.L1HeadersRlp[index].Memory);
            staged.Append(segment, in rules);
        }
        return new NativeStreamingEvidence(staged, _limit);
    }

    internal NativeStreamingEvidence Prefix(int count) => count == Count ? this : new(_headers.Prefix(count), _limit);

    internal NativeL1Header Endpoint => _headers.Endpoint;

    internal void VerifyAnchor(in NativeEvidenceAnchor anchor) => _headers.VerifyAnchor(in anchor);

    internal void VerifyPrefix(int count, in NativeEvidenceAnchor anchor)
    {
        if (count <= 0 || count > Count) throw NativeL1Header.Invalid("invalid selected block L1 checkpoint");
        ulong number = Endpoint.Number - (ulong)(Count - count);
        _headers.VerifyOrigin(number, _headers.At(number).Hash, in anchor);
    }

    internal RollupXObservationReplay CreateObservationReplay(NativeGenesisSnapshot profile, ILogManager logs) => new(profile, _headers, logs);

    internal EezStatelessExecutor CreateExecutor(NativeGenesisSnapshot profile, ILogManager logs) => new(profile, _headers, logs);
}
