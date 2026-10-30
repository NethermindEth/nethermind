// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Consensus.Transactions;

/// <summary>
/// Remembers the base fee of the last parent header and spec a filter was asked about, so that checking many
/// transactions against one parent, as block building does, computes it once rather than once per transaction.
/// </summary>
/// <remarks>
/// A filter can be called from several threads at once, such as tx-pool admission threads that see a new head, so the
/// cached pair is one immutable entry replaced by reference: a reader sees either the previous entry or the new one,
/// never a mix of the two.
/// The key is the parent and spec instances. While they stay the same, the inputs the spec's base-fee calculator reads
/// from them (the parent's fee fields, the spec's fee parameters) must not change; block-tree headers and release specs
/// do not change them. A new head, a different parent or a different spec (a fork boundary) computes the base fee again.
/// </remarks>
internal sealed class ParentBaseFeeCache
{
    private Entry? _last;

    public UInt256 Get(BlockHeader? parent, IReleaseSpec spec)
    {
        Entry? last = Volatile.Read(ref _last);
        if (last is not null && ReferenceEquals(last.Parent, parent) && ReferenceEquals(last.Spec, spec))
        {
            return last.BaseFee;
        }

        UInt256 baseFee = BaseFeeCalculator.Calculate(parent!, spec);
        if (parent is not null)
        {
            Volatile.Write(ref _last, new Entry(parent, spec, baseFee));
        }

        return baseFee;
    }

    private sealed class Entry(BlockHeader parent, IReleaseSpec spec, in UInt256 baseFee)
    {
        public readonly BlockHeader Parent = parent;
        public readonly IReleaseSpec Spec = spec;
        public readonly UInt256 BaseFee = baseFee;
    }
}
