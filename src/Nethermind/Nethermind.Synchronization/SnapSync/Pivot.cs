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

        public long Diff
        {
            get
            {
                return _blockTree.BestSuggestedHeader?.Number ?? 0 - _bestHeader.Number;
            }
        }

        public Pivot(IBlockTree blockTree, ILogManager logManager)
        {
            _blockTree = blockTree;
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public BlockHeader GetPivotHeader()
        {
            if(_bestHeader is null || _blockTree.BestSuggestedHeader?.Number - _bestHeader.Number >= Constants.MaxDistanceFromHead - 20)
            {
                _logger.Warn($"SNAP - Pivot changed from {_bestHeader?.Number} to {_blockTree.BestSuggestedHeader?.Number}");

                _bestHeader = _blockTree.BestSuggestedHeader;
            }

            var currentHeader = _blockTree.FindHeader(_bestHeader.Number);
            if(currentHeader.StateRoot != _bestHeader.StateRoot)
            {
                _logger.Warn($"SNAP - Pivot:{_bestHeader.StateRoot}, Current:{currentHeader.StateRoot}");
            }

            return _bestHeader;
        }
    }
}
