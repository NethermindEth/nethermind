using System;
using System.Linq;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Consensus.Transactions;
using Nethermind.Mev.Data;
using Nethermind.Mev.Source;

namespace Nethermind.AccountAbstraction.Bundler
{
    public class PeriodicMevBundler : IBundler
    {
        private IBundleTrigger _trigger;
        private ITxSource _txSource;
        private IBundlePool _bundlePool;
        private IBlockTree _blockTree;

        public PeriodicMevBundler(IBundleTrigger trigger, ITxSource txSource, IBundlePool bundlePool, IBlockTree blockTree)
        {
            _trigger = trigger;
            _txSource = txSource;
            _bundlePool = bundlePool;
            _blockTree = blockTree;

            _trigger.TriggerBundle += (_, _) => Bundle();
        }

        public void Bundle()
        {
            // turn ops into txs
            IEnumerable<BundleTransaction> transactions =
                _txSource.GetTransactions(_blockTree.Head!.Header, _blockTree.Head.GasLimit)
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
            MevBundle bundle = new(_blockTree.Head.Header.Number + 1, transactions.ToArray());

            // send MevBundle using SendBundle()
            bool result = _bundlePool.AddBundle(bundle);
        }
    }
}
