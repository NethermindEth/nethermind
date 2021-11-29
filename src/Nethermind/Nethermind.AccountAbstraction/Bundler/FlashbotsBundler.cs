
using System.Linq;
using System.Collections.Generic;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Consensus;
using Nethermind.Logging;
using Nethermind.AccountAbstraction.Flashbots;
using System.Net.Http;

namespace Nethermind.AccountAbstraction.Bundler
{
    public class FlashbotsBundler : IBundler
    {
        private readonly IBundleTrigger _trigger;
        private readonly ITxSource _txSource;
        private readonly ISigner _signer;
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;
        private readonly string _flashbotsEndpoint;

        public FlashbotsBundler(IBundleTrigger trigger, ITxSource txSource, ISigner signer, ILogger logger, string flashbotsEndpoint)
        {
            _trigger = trigger;
            _txSource = txSource;
            _signer = signer;
            _logger = logger;
            _flashbotsEndpoint = flashbotsEndpoint;
            _httpClient = new HttpClient();

            _trigger.TriggerBundle += OnTriggerBundle;
        }

        public void OnTriggerBundle(object? sender, BundleUserOpsEventArgs args)
        {
            Bundle(args.Head);
        }

        public void Bundle(Block head)
        {
            FlashbotsSender flashbotsSender = new(_httpClient, _signer, _logger);

            // turn ops into txs
            IEnumerable<Transaction> transaction =
                _txSource.GetTransactions(head.Header, head.GasLimit);
            string[] transactionArray = transaction.Select(pkg => pkg.ToString()).ToArray();

            // turn txs into MevBundle
            FlashbotsSender.MevBundle bundle = new(head.Header.Number + 1, transactionArray);

            // send MevBundle using SendBundle()
            flashbotsSender.SendBundle(bundle, _flashbotsEndpoint).ContinueWith(_ => _);
        }
    }
}
