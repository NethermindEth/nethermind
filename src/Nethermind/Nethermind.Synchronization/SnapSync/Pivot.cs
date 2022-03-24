using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.State.Snap;

namespace Nethermind.Synchronization.SnapSync
{
    internal class Pivot
    {
        private readonly IBlockTree _blockTree;
        private BlockHeader _bestHeader;

        public Pivot(IBlockTree blockTree)
        {
            _blockTree = blockTree;
        }

        public BlockHeader GetPivotHeader()
        {
            if(_bestHeader is null || _blockTree.BestSuggestedHeader?.Number - _bestHeader.Number >= Constants.MaxDistanceFromHead)
            {
                _bestHeader = _blockTree.BestSuggestedHeader;
            }

            return _bestHeader;
        }
    }
}
