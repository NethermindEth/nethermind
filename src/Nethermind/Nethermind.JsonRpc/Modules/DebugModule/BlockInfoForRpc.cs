// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.DebugModule
{
    public class BlockInfoForRpc
    {
        public BlockInfoForRpc(BlockInfo blockInfo)
        {
            BlockHash = blockInfo.BlockHash;
            TotalDifficulty = blockInfo.TotalDifficulty;
            WasProcessed = blockInfo.WasProcessed;
            IsFinalized = blockInfo.IsFinalized;
        }

        public Keccak BlockHash { get; set; }

        public UInt256 TotalDifficulty { get; set; }

        public bool WasProcessed { get; set; }

        public bool IsFinalized { get; set; }
    }
}
