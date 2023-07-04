// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Crypto;
using Nethermind.State;

namespace Nethermind.Blockchain
{
    public class ChainHeadReadOnlyStateProvider : SpecificBlockReadOnlyStateProvider
    {
        private readonly IBlockFinder _blockFinder;

        public ChainHeadReadOnlyStateProvider(IBlockFinder blockFinder, IStateReader stateReader) : base(stateReader)
        {
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
        }

        public override Keccak StateRoot => _blockFinder.Head?.StateRoot ?? Keccak.EmptyTreeHash;
    }
}
