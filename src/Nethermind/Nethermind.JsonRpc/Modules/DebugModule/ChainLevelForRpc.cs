// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;

namespace Nethermind.JsonRpc.Modules.DebugModule
{
    public class ChainLevelForRpc
    {
        public ChainLevelForRpc(ChainLevelInfo chainLevelInfo)
        {
            HasBlockOnMainChain = chainLevelInfo.HasBlockOnMainChain;
            BlockInfos = chainLevelInfo.BlockInfos.Select(bi => new BlockInfoForRpc(bi)).ToArray();
        }

        public BlockInfoForRpc[] BlockInfos { get; set; }

        public bool HasBlockOnMainChain { get; set; }
    }
}
