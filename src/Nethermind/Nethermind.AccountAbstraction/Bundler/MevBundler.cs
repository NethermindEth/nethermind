using System.Linq;
using System.Collections.Generic;
using Nethermind.Consensus.Transactions;
using Nethermind.Mev.Data;
using Nethermind.Mev.Source;
using Nethermind.Core;

namespace Nethermind.AccountAbstraction.Bundler
{
    public class MevBundler : IBundler
    {
        private IBundleTrigger _trigger;
        private ITxSource _txSource;
        private IBundlePool _bundlePool;

        public MevBundler(IBundleTrigger trigger, ITxSource txSource, IBundlePool bundlePool)
        {
            _trigger = trigger;
            _txSource = txSource;
            _bundlePool = bundlePool;

            _trigger.TriggerBundle += OnTriggerBundle;
        }

        public void OnTriggerBundle(object? sender, BundleUserOpsEventArgs args)
        {
            Bundle(args.Head);
        }

        public void Bundle(Block head)
        {
            // turn ops into txs
            IEnumerable<BundleTransaction> transactions =
                _txSource.GetTransactions(head.Header, head.GasLimit)
                .Select(tx => new BundleTransaction
                {
                    GasPrice = tx.GasPrice,
                    GasLimit = tx.GasLimit,
                    To = tx.To,
                    ChainId = tx.ChainId,
                    Nonce = tx.Nonce,
                    Value = tx.Value,
                    Data = tx.Data,
                    Type = tx.Type,
                    DecodedMaxFeePerGas = tx.DecodedMaxFeePerGas,
                    SenderAddress = tx.SenderAddress,
                    Hash = tx.Hash,
                });

            // turn txs into MevBundle
            MevBundle bundle = new(head.Header.Number + 1, transactions.ToArray());

            // add MevBundle to MevPlugin bundle pool
            bool result = _bundlePool.AddBundle(bundle);
        }
    }
}
