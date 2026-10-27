// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>Request-local consecutive L1 evidence, with atomic segment admission and retained prefixes.</summary>
/// <remarks>The owning execution session serializes mutations. No network or fallback source participates.</remarks>
internal sealed class NativeL1HeaderChain
{
    internal const int MaxHeaders = 131_072;
    private readonly List<NativeL1Header> _headers;
    private readonly int _limit;
    internal int Count => _headers.Count;
    internal ulong FirstNumber => Count != 0 ? _headers[0].Number : throw NativeL1Header.Invalid("missing headers");
    internal NativeL1Header Endpoint => Count != 0 ? _headers[^1] : throw NativeL1Header.Invalid("missing headers");

    internal NativeL1HeaderChain(int limit = MaxHeaders)
    {
        if (limit is < 1 or > MaxHeaders) throw new ArgumentOutOfRangeException(nameof(limit));
        _headers = [];
        _limit = limit;
    }

    private NativeL1HeaderChain(List<NativeL1Header> headers, int limit) => (_headers, _limit) = (headers, limit);

    internal void Append(IReadOnlyList<ReadOnlyMemory<byte>> segment, in NativeL1HeaderRules rules)
    {
        if (segment.Count > _limit - Count) throw NativeL1Header.Invalid("header count limit");
        using ArrayPoolListRef<NativeL1Header> checkedHeaders = new(segment.Count);
        NativeL1Header? previous = Count == 0 ? null : _headers[^1];
        for (int index = 0; index < segment.Count; index++)
        {
            NativeL1Header header = NativeL1Header.Decode(segment[index].Span, in rules);
            EnsureNext(previous, header);
            checkedHeaders.Add(header);
            previous = header;
        }
        _headers.AddRange(checkedHeaders.AsSpan());
    }

    internal void AppendChecked(ReadOnlySpan<NativeL1Header> segment)
    {
        if (segment.Length > _limit - Count) throw NativeL1Header.Invalid("header count limit");
        NativeL1Header? previous = Count == 0 ? null : _headers[^1];
        foreach (NativeL1Header header in segment)
        {
            EnsureNext(previous, header);
            previous = header;
        }
        _headers.AddRange(segment);
    }

    internal NativeL1HeaderChain Range(ulong firstNumber, ulong lastNumber)
    {
        _ = At(firstNumber);
        _ = At(lastNumber);
        if (lastNumber < firstNumber) throw NativeL1Header.Invalid("reversed history range");
        int offset = checked((int)(firstNumber - FirstNumber));
        return new NativeL1HeaderChain(_headers.GetRange(offset, checked((int)(lastNumber - firstNumber + 1))), _limit);
    }

    private static void EnsureNext(NativeL1Header? previous, NativeL1Header header)
    {
        if (previous is not null && (previous.Number == ulong.MaxValue || header.Number != previous.Number + 1 ||
            header.ParentHash != previous.Hash || header.Timestamp <= previous.Timestamp)) throw NativeL1Header.Invalid("nonconsecutive ancestry");
    }

    internal NativeL1Header At(ulong number)
    {
        if (Count == 0 || number < _headers[0].Number || number - _headers[0].Number >= (ulong)Count)
            throw NativeL1Header.Invalid("header outside supplied evidence");
        return _headers[(int)(number - _headers[0].Number)];
    }

    internal NativeL1Header Select(ulong cutoff)
    {
        int low = 0, high = Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (_headers[middle].Timestamp <= cutoff) low = middle + 1;
            else high = middle;
        }
        return low == 0 ? throw NativeL1Header.Invalid("missing source at cutoff") : _headers[low - 1];
    }

    internal NativeL1HeaderChain Prefix(int count) => count < 0 || count > Count
        ? throw NativeL1Header.Invalid("checkpoint exceeds retained evidence")
        : new NativeL1HeaderChain(_headers.GetRange(0, count), _limit);

    internal void VerifyAnchor(in NativeEvidenceAnchor anchor)
    {
        NativeL1Header end = Endpoint;
        if (anchor.Number == ulong.MaxValue || anchor.Hash == default || anchor.Number != end.Number || anchor.Hash != end.Hash ||
            (anchor.TargetTimestamp is { } target ? target <= end.Timestamp : anchor.Number == 0))
            throw NativeL1Header.Invalid("evidence does not end at exact anchor");
    }

    internal void VerifyOrigin(ulong number, in ValueHash256 selected, in NativeEvidenceAnchor anchor)
    {
        VerifyAnchor(in anchor);
        if (At(number).Hash != selected) throw NativeL1Header.Invalid("evidence contradicts recorded origin");
    }
}

/// <summary>Claimed offline anchor; only the matching on-chain manager context grants authorization.</summary>
internal readonly record struct NativeEvidenceAnchor(ulong Number, ValueHash256 Hash, ulong? TargetTimestamp)
{
    internal ulong Selector => TargetTimestamp is null ? Number : ulong.MaxValue;
}
