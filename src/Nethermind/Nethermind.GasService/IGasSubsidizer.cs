using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.GasService
{
    public interface IGasSubsidizer
    {
        /// <summary>
        /// Wraps the <paramref name="signedTx"/> in a subsidized transaction and returns the wrapped transaction object.
        /// </summary>
        /// <param name="signedTx">A signed transaction to subsidize.</param>
        /// <param name="broadcast"><value>True</value> if the wrapped transaction should be broadcast to the network by the subsidizer, otherwise <value>false</value></param>
        /// <returns>Status of the subsidy with a wrapped transaction or <value>null</value> if transaction not yet created.</returns>
        Task<SubsidyResult> Subsidize(Transaction signedTx, bool broadcast);
        Task<SubsidyResult> Subsidize(Address recipient, UInt256 value, byte[]? callData = null);
    }
}