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

using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Consensus
{
    public class BlockPreparationContextService : IBlockPreparationContextService
    {
        private BlockPreparationContext? _currentContext;
        private ILogger _logger;

        public BlockPreparationContextService(ILogManager logManager)
        {
            _logger = logManager.GetClassLogger();
        }
        
        public void SetContext(UInt256 baseFee, long blockNumber)
        {
            _currentContext = new BlockPreparationContext(baseFee, blockNumber);
        }

        public UInt256 BaseFee
        {
            get
            {
                if (_currentContext == null)
                {
                    if (_logger.IsWarn) _logger.Warn("Cannot use block preparation context, because it wasn't set");
                    return UInt256.Zero;
                }
                
                return _currentContext.Value.BaseFee;
            }
        }

        public long BlockNumber         
        {
            get
            {
                if (_currentContext == null)
                {
                    if (_logger.IsWarn) _logger.Warn("Cannot use block preparation context, because it wasn't set");
                    return 0;
                }
                
                return _currentContext.Value.BlockNumber;
            }
        }
    }
}
