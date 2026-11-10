// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus;
using Nethermind.TxPool;

namespace Nethermind.Merge.AuRa
{
    public class AuRaMergeTxFilter(IPoSSwitcher poSSwitcher, ITxFilter preMergeTxFilter, ITxFilter? postMergeTxFilter = null) : ITxFilter
    {
        private readonly IPoSSwitcher _poSSwitcher = poSSwitcher;
        private readonly ITxFilter _preMergeTxFilter = preMergeTxFilter;
        private readonly ITxFilter _postMergeTxFilter = postMergeTxFilter ?? NullTxFilter.Instance;

        public AcceptTxResult IsAllowed(Transaction tx, in TxFilterContext context) =>
            _poSSwitcher.IsPostMerge(context.Parent)
                ? _postMergeTxFilter.IsAllowed(tx, in context)
                : _preMergeTxFilter.IsAllowed(tx, in context);
    }
}
