// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Core;

namespace Nethermind.Blockchain.Test
{
    public class NullRecoveryStep : IBlockPreprocessorStep
    {
        private NullRecoveryStep()
        {
        }

        public static NullRecoveryStep Instance = new();

        public void RecoverData(Block block)
        {
        }
    }
}
