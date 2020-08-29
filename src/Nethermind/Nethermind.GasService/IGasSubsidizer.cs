using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.GasService
{
    public interface IGasSubsidizer
    {
        Task<Transaction> Subsidize(Transaction signedTx);
        Task BroadcastSubsidized(Transaction signedTx);
        Task BroadcastSubsidized(byte[] callData, Address recipient, UInt256 value);
    }
}