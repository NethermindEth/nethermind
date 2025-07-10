// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State;

namespace Nethermind.Blockchain
{
    public class ChainHeadReadOnlyStateProvider(IBlockFinder blockFinder, IStateReader stateReader)
        : SpecificBlockReadOnlyStateProvider(stateReader, null)
    {
        public override BlockHeader? BaseBlock => blockFinder.Head?.Header;
    }
}
