//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using Nethermind.Blockchain.Processing;
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
            if (block.Header.Author != null) return;
            block.Header.Author = _snapshotManager.GetBlockSealer(block.Header);
        }
    }
}
