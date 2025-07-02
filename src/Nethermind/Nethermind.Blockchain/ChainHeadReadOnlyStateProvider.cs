// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Crypto;
using Nethermind.State;

namespace Nethermind.Blockchain
{
    public class ChainHeadReadOnlyStateProvider(IBlockFinder blockFinder, IStateReader stateReader)
        : SpecificBlockReadOnlyStateProvider(stateReader)
    {
        private readonly IBlockFinder _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));

        public override Hash256 StateRoot => _blockFinder.Head?.StateRoot ?? Keccak.EmptyTreeHash;
    }
}
