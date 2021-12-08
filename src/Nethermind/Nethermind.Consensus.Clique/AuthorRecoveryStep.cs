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
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Clique
{
    public class AuthorRecoveryStep : IBlockPreprocessorStep
    {
        private readonly ISnapshotManager _snapshotManager;
        private readonly ISpecProvider _specProvider;

        [Todo(Improve.Refactor, "Strong coupling here")]
        public AuthorRecoveryStep(ISnapshotManager snapshotManager, ISpecProvider specProvider)
        {
            _snapshotManager = snapshotManager ?? throw new ArgumentNullException(nameof(snapshotManager));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        }

        public void RecoverData(Block block)
        {
            // ToDo after refactoring PostMerge SpecProvider, change this condition: isPos(block)
            if (block.Header.Author != null || block.Header.IsPostMerge || block.Number >= _specProvider.MergeBlockNumber) return;
            block.Header.Author = _snapshotManager.GetBlockSealer(block.Header);
        }
    }
}
