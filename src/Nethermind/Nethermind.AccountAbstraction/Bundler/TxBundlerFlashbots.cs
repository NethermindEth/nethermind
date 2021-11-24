using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.AccountAbstraction.Flashbots;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.AccountAbstraction.Bundler
{
    public class TxBundlerFlashbots : ITxBundler
    {
        public bool IsRunning { get; private set; }

        private ITxBundlingTrigger _trigger;
        private ITxBundleSource _source;
        private IGasLimitProvider _gasLimitProvider;
        private IBlockTree _blockTree;
        private ISigner _signer;
        private ILogger _logger;
        private string _flashbotsEndpoint;

        private CancellationTokenSource? _bundlerCancellationToken;

        public TxBundlerFlashbots
        (
             ITxBundlingTrigger trigger, ITxBundleSource source,
             IGasLimitProvider gasLimitProvider, IBlockTree blockTree,
             ISigner signer, ILogger logger, string flashbotsEndpoint
        )
        {
            _trigger = trigger;
            _source = source;
            _gasLimitProvider = gasLimitProvider;
            _blockTree = blockTree;
            _signer = signer;
            _logger = logger;
            _flashbotsEndpoint = flashbotsEndpoint;
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
            BlockHeader head = _blockTree.Head!.Header;
            Transaction? tx = _source.GetTransaction(head, _gasLimitProvider.GetGasLimit());
            if (tx is null) return;

            FlashbotsSender flashbotsSender = new(new HttpClient(), _signer, _logger);

            FlashbotsSender.MevBundle bundle =
                new(_blockTree.Head.Header.Number + 1, new[] { Rlp.Encode(tx).ToString() });
            flashbotsSender.SendBundle(bundle, _flashbotsEndpoint).ContinueWith(_ => _);
        }
    }
}
