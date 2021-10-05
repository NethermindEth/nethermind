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
// 

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin
{
    public class MergeDependent<TImplementation> : IMergeDependent<TImplementation>
    {
        private readonly ILogger _logger;

        private Stage _currentStage = Stage.BeforeTheMerge;

        public MergeDependent(IBlockTree blockTree, ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger<MergeDependent<TImplementation>>()
                      ?? throw new ArgumentNullException(nameof(logManager));
            blockTree.NewHeadBlock += BlockTreeOnNewHeadBlock;
        }
        
        public MergeDependent(
            IBlockTree? blockTree,
            TImplementation? beforeTheMerge,
            TImplementation? afterTheMerge,
            ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger<MergeDependent<TImplementation>>()
                      ?? throw new ArgumentNullException(nameof(logManager));

            _implementations[Stage.BeforeTheMerge] = beforeTheMerge
                                                     ?? throw new ArgumentNullException(nameof(beforeTheMerge));
            _implementations[Stage.AfterTheMerge] = afterTheMerge
                                                    ?? throw new ArgumentNullException(nameof(afterTheMerge));
            
            if (blockTree == null) throw new ArgumentNullException(nameof(blockTree));
            blockTree.NewHeadBlock += BlockTreeOnNewHeadBlock;
        }

        private void BlockTreeOnNewHeadBlock(object? sender, BlockEventArgs e)
        {
            if (e.Block.TotalDifficulty!.Value > 10)
            {
                if(_logger.IsInfo) _logger.Info("The Merge is about to happen");
                _currentStage = Stage.AfterTheMerge;
                TheMergeHappened?.Invoke(this, EventArgs.Empty);
                if(_logger.IsInfo) _logger.Info("The Merge has just happened");
            }
        }

        private readonly Dictionary<Stage, TImplementation> _implementations = new();

        public void Register(TImplementation implementation, Stage stage)
        {
            _implementations[stage] = implementation;
        }

        public TImplementation Resolve()
        {
            Stage stage = _currentStage;
            if (stage == Stage.AfterTheMerge && !_implementations.ContainsKey(stage))
            {
                // this means that we are operating on a network without the Merge switch defined
                // (or that we initialized the whole thing incorrectly)
                // in both cases we want to return the before the Merge configuration
                // (in the latter case we will simply deal with the incorrect behaviour)
                return _implementations[Stage.BeforeTheMerge];
            }

            return _implementations[stage];
        }

        /// <summary>
        /// 
        /// </summary>
        public event EventHandler<EventArgs>? TheMergeHappened;
    }
}
