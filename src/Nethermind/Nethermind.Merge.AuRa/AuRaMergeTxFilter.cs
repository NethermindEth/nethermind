using Nethermind.Core;
using Nethermind.TxPool;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus;

namespace Nethermind.Merge.AuRa
{
    public class AuRaMergeTxFilter : ITxFilter
    {
        private readonly IPoSSwitcher _poSSwitcher;
        private readonly ITxFilter _txFilter;

        public AuRaMergeTxFilter(IPoSSwitcher poSSwitcher, ITxFilter txFilter)
        {
            _poSSwitcher = poSSwitcher;
            _txFilter = txFilter;
        }

        public AcceptTxResult IsAllowed(Transaction tx, BlockHeader parentHeader)
        {
            if (_poSSwitcher.IsPostMerge(parentHeader))
                return NullTxFilter.Instance.IsAllowed(tx, parentHeader);
            return _txFilter.IsAllowed(tx, parentHeader);
        }
    }
}
