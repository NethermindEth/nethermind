using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.AccountAbstraction.Source;
using Nethermind.TxPool;
using Nethermind.Blockchain;

namespace Nethermind.AccountAbstraction.Bundler
{
    public class TxBundler : ITxBundler
    {
        public bool IsRunning { get; private set; }

        private ITxBundlingTrigger _trigger;
        private ITxBundleSource _source;
        private IGasLimitProvider _gasLimitProvider;
        private ITxSender _txSender;
        private IBlockTree _blockTree;

        private CancellationTokenSource? _bundlerCancellationToken;

        public TxBundler
        (
             ITxBundlingTrigger trigger, ITxBundleSource source,
             IGasLimitProvider gasLimitProvider, ITxSender txSender,
             IBlockTree blockTree)
        {
            _trigger = trigger;
            _source = source;
            _gasLimitProvider = gasLimitProvider;
            _txSender = txSender;
            _blockTree = blockTree;
        }

        public void Start()
        {
            _bundlerCancellationToken = new CancellationTokenSource();
            IsRunning = true;
            _trigger.TriggerTxBundling += OnTriggerTxBundling;
        }

        public Task StopAsync()
        {
            _bundlerCancellationToken?.Cancel();
            IsRunning = false;
            _trigger.TriggerTxBundling -= OnTriggerTxBundling;
            _bundlerCancellationToken?.Dispose();
            return Task.CompletedTask;
        }

        private void OnTriggerTxBundling(object? sender, EventArgs e)
        {
            BlockHeader head = _blockTree!.Head!.Header;
            Transaction? tx = _source.GetTransaction(head, _gasLimitProvider.GetGasLimit());
            if (tx is null) return;
            _txSender.SendTransaction(tx, TxPool.TxHandlingOptions.None);
        }
    }
}
