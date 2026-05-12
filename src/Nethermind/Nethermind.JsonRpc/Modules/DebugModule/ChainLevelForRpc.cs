// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;

namespace Nethermind.JsonRpc.Modules.DebugModule
{
    public class ChainLevelForRpc(ChainLevelInfo chainLevelInfo)
    {
        public BlockInfoForRpc[] BlockInfos { get; set; } = chainLevelInfo.BlockInfos.Select(static bi => new BlockInfoForRpc(bi)).ToArray();

        public bool HasBlockOnMainChain { get; set; } = chainLevelInfo.HasBlockOnMainChain;
    }
}
