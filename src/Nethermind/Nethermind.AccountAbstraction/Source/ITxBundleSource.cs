using Nethermind.Core;

namespace Nethermind.AccountAbstraction.Source
{
    public interface ITxBundleSource
    {
        Transaction? GetTransaction(BlockHeader head, ulong gasLimit);
    }
}
