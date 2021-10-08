using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public interface IRlpNdmDecoder<T> : IRlpStreamDecoder<T>, IRlpObjectDecoder<T>
    {
    }
}
