// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Attributes;

namespace Nethermind.Consensus.Clique
{
    public class AuthorRecoveryStep : IBlockPreprocessorStep
    {
        private readonly ISnapshotManager _snapshotManager;

        [Todo(Improve.Refactor, "Strong coupling here")]
        public AuthorRecoveryStep(ISnapshotManager snapshotManager)
        {
            _snapshotManager = snapshotManager ?? throw new ArgumentNullException(nameof(snapshotManager));
        }

        public void RecoverData(Block block)
        {
            if (block.Header.Author is not null) return;
            block.Header.Author = _snapshotManager.GetBlockSealer(block.Header);
        }
    }
}
