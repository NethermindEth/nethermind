using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.AccountAbstraction.Source;

namespace Nethermind.AccountAbstraction.Bundler
{
    public class TxBundler : ITxBundler
    {
        public bool IsRunning { get; private set; }

        private ITxBundlingTrigger _trigger;
        private ITxBundleSource _source;
        private IGasLimitProvider _gasLimitProvider;
        private INethermindApi _api;

        private CancellationTokenSource? _bundlerCancellationToken;

        public TxBundler(ITxBundlingTrigger trigger, ITxBundleSource source, IGasLimitProvider gasLimitProvider, INethermindApi api)
        {
            _trigger = trigger;
            _source = source;
            _gasLimitProvider = gasLimitProvider;
            _api = api;
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
            BlockHeader head = _api.BlockTree!.Head!.Header;
            Transaction? tx = _source.GetTransaction(head, _gasLimitProvider.GetGasLimit());
            if (tx is null) return;
            _api.TxSender!.SendTransaction(tx, TxPool.TxHandlingOptions.None);
        }
    }
}
