using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.State.Snap;

namespace Nethermind.Synchronization.SnapSync
{
    internal class Pivot
    {
        private readonly IBlockTree _blockTree;
        private BlockHeader _bestHeader;
        private readonly ILogger _logger;

        public Pivot(IBlockTree blockTree, ILogManager logManager)
        {
            _blockTree = blockTree;
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public BlockHeader GetPivotHeader()
        {
            if(_bestHeader is null || _blockTree.BestSuggestedHeader?.Number - _bestHeader.Number >= 20) //Constants.MaxDistanceFromHead - 10
            {
                _logger.Warn($"SNAP - Pivot changed from {_bestHeader?.Number} to {_blockTree.BestSuggestedHeader?.Number}");

                _bestHeader = _blockTree.BestSuggestedHeader;
            }

            return _bestHeader;
        }
    }
}
