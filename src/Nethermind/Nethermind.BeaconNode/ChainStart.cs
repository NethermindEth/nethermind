using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Types;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode
{
    public class ChainStart
    {
        private readonly ForkChoice _forkChoice;
        private readonly Genesis _genesis;
        private readonly ILogger _logger;

        public ChainStart(ILogger<ChainStart> logger,
            Genesis genesis,
            ForkChoice forkChoice)
        {
            _logger = logger;
            _genesis = genesis;
            _forkChoice = forkChoice;
        }

        public async Task<bool> TryGenesisAsync(Hash32 eth1BlockHash, ulong eth1Timestamp, IList<Deposit> deposits)
        {
            return await Task.Run(() =>
            {
                if (_logger.IsDebug()) _logger.LogDebug(Event.TryGenesis, "Try genesis with ETH1 block {Eth1BlockHash}, time {Eth1Timestamp}, with {DepositCount} deposits.", eth1BlockHash, eth1Timestamp, deposits.Count);

                var candidateState = _genesis.InitializeBeaconStateFromEth1(eth1BlockHash, eth1Timestamp, deposits);
                if (_genesis.IsValidGenesisState(candidateState))
                {
                    var genesisState = candidateState;
                    _ = _forkChoice.GetGenesisStore(genesisState);
                    return true;
                }
                return false;
            });
        }
    }
}
