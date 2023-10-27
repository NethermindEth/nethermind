// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Synchronization
{
    public interface IPivot
    {
        long PivotNumber { get; }

        Hash256? PivotHash { get; }

        Hash256? PivotParentHash { get; }

        UInt256? PivotTotalDifficulty { get; }

        long PivotDestinationNumber { get; }
    }
}
