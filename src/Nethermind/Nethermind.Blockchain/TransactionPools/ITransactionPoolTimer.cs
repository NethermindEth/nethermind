using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.TransactionPools
{
    public interface ITransactionPoolTimer
    {
        UInt256 CurrentTimestamp { get; }
    }
}