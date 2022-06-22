using Nethermind.Consensus.Transactions;

namespace Nethermind.Consensus.AuRa.InitializationSteps
{
    public interface ITxFilterDecorator
    {
        bool IsApplicable(ITxFilter txFilter);
        ITxFilter Decorate(ITxFilter txFilter);
    }
}
