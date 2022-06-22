using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Transactions;

namespace Nethermind.Merge.AuRa
{
    public class AuRaMergeTxFilterDecorator : ITxFilterDecorator
    {
        private readonly IPoSSwitcher _poSSwitcher;

        public AuRaMergeTxFilterDecorator(IPoSSwitcher poSSwitcher)
        {
            _poSSwitcher = poSSwitcher;
        }

        public ITxFilter Decorate(ITxFilter txFilter)
        {
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
