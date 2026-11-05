// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.DebugModule
{
    public class BlockInfoForRpc(BlockInfo blockInfo)
    {
        public Hash256 BlockHash { get; set; } = blockInfo.BlockHash;

        public UInt256 TotalDifficulty { get; set; } = blockInfo.TotalDifficulty;

        public bool WasProcessed { get; set; } = blockInfo.WasProcessed;

        public bool IsFinalized { get; set; } = blockInfo.IsFinalized;
    }
}
