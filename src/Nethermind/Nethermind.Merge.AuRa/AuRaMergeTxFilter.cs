// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.TxPool;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus;

namespace Nethermind.Merge.AuRa
{
    public class AuRaMergeTxFilter : ITxFilter
    {
        private readonly IPoSSwitcher _poSSwitcher;
        private readonly ITxFilter _preMergeTxFilter;
        private readonly ITxFilter _postMergeTxFilter;

        public AuRaMergeTxFilter(IPoSSwitcher poSSwitcher, ITxFilter preMergeTxFilter, ITxFilter? postMergeTxFilter = null)
        {
            _poSSwitcher = poSSwitcher;
            _preMergeTxFilter = preMergeTxFilter;
            _postMergeTxFilter = postMergeTxFilter ?? NullTxFilter.Instance;
        }

        public AcceptTxResult IsAllowed(Transaction tx, BlockHeader parentHeader) =>
            _poSSwitcher.IsPostMerge(parentHeader)
                ? _postMergeTxFilter.IsAllowed(tx, parentHeader)
                : _preMergeTxFilter.IsAllowed(tx, parentHeader);
    }
}
