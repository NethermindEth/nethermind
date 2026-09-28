// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>The block's own prefix laid over what the consecutive blocks before it wrote; a read that misses both
/// belongs to the parent state.</summary>
internal sealed class ChainedReadOverlay(IStateReadOverlay prefix, IStateReadOverlay earlierBlocks) : IStateReadOverlay
{
    public bool TryGetAccount(Address address, Account? underlying, out Account? overlaid)
    {
        bool earlier = earlierBlocks.TryGetAccount(address, underlying, out Account? basis);
        if (!earlier) basis = underlying;
        if (prefix.TryGetAccount(address, basis, out overlaid)) return true;

        overlaid = basis;
        return earlier;
    }

    public bool TryGetStorage(Address address, in UInt256 index, out UInt256 value) =>
        prefix.TryGetStorage(address, in index, out value) || earlierBlocks.TryGetStorage(address, in index, out value);

    public bool HasStorage(Address address) => prefix.HasStorage(address) || earlierBlocks.HasStorage(address);
}
