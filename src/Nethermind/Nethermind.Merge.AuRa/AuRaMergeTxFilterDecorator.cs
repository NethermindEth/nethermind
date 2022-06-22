using Nethermind.Api;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Transactions;

namespace Nethermind.Merge.AuRa
{
    public class AuRaMergeTxFilterDecorator : ITxFilterDecorator
    {
        private readonly INethermindApi _api;
        private readonly IPoSSwitcher _poSSwitcher;

        public AuRaMergeTxFilterDecorator(INethermindApi api, IPoSSwitcher poSSwitcher)
        {
            _api = api;
            _poSSwitcher = poSSwitcher;
        }

        public ITxFilter Decorate(ITxFilter txFilter)
        {
            if (txFilter is MinGasPriceTxFilter)
                return TxFilterBuilders.CreateStandardMinGasPriceTxFilter(
                        _api.Config<IMiningConfig>(),
                        _api.SpecProvider!);
            return new AuRaMergeTxFilter(_poSSwitcher, txFilter);
        }

        public bool IsApplicable(ITxFilter txFilter)
        {
            // TODO: should it just return true?
            return txFilter switch
            {
                LocalTxFilter _ => true,
                MinGasPriceContractTxFilter _ => true,
                ServiceTxFilter _ => true,
                TxCertifierFilter _ => true,
                PermissionBasedTxFilter _ => true,
                _ => false
            };
        }
    }
}
