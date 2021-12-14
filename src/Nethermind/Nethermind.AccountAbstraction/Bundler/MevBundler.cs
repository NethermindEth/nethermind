using System.Linq;
using System.Collections.Generic;
using Nethermind.Consensus.Transactions;
using Nethermind.Mev.Data;
using Nethermind.Mev.Source;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.AccountAbstraction.Bundler
{
    public class MevBundler : IBundler
    {
        private IBundleTrigger _trigger;
        private ITxSource _txSource;
        private IBundlePool _bundlePool;
        private ILogger _logger;

        public MevBundler(IBundleTrigger trigger, ITxSource txSource, IBundlePool bundlePool, ILogger logger)
        {
            _trigger = trigger;
            _txSource = txSource;
            _bundlePool = bundlePool;
            _logger = logger;
            
            _logger.Info("AA: Starting Mev Bundler...");

            _trigger.TriggerBundle += OnTriggerBundle;
        }

        public void OnTriggerBundle(object? sender, BundleUserOpsEventArgs args)
        {
            _logger.Info("Trigger maybe Works");
            Bundle(args.Head);
        }

        public void Bundle(Block head)
        {
            _logger.Info("Trigger Works");
            // turn ops into txs
            var transactions =
                _txSource.GetTransactions(head.Header, head.GasLimit)
                .Select(tx => new BundleTransaction
                {
                    ChainId = tx.ChainId,
                    Type = tx.Type,

                    Nonce = tx.Nonce,
                    GasPrice = tx.GasPrice,
                    GasBottleneck = tx.GasBottleneck,
                    DecodedMaxFeePerGas = tx.DecodedMaxFeePerGas,
                    GasLimit = tx.GasLimit,
                    To = tx.To,
                    Value = tx.Value,
                    Data = tx.Data,
                    SenderAddress = tx.SenderAddress,
                    Signature = tx.Signature,
                    Hash = tx.Hash,
                    DeliveredBy = tx.DeliveredBy,
                    Timestamp = tx.Timestamp,
                    AccessList = tx.AccessList,
                })
                .ToArray();

            if (transactions.Length == 0) return;

            // turn txs into MevBundle
            MevBundle bundle = new(head.Header.Number + 1, transactions);

            _logger.Info("Trying to add bundle from AA to MEV bundle pool");
            // add MevBundle to MevPlugin bundle pool
            bool result = _bundlePool.AddBundle(bundle);

            if (result) _logger.Info("Bundle from AA successfuly added to MEV bundle pool");
            else _logger.Info("Bundle from AA failed to be added to MEV bundle pool");
        }
    }
}
