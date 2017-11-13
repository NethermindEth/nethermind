using System.Numerics;

namespace Nevermind.Evm.Precompiles
{
    public interface IPrecompiledContract
    {
        BigInteger Address { get; }

        ulong BaseGasCost();

        ulong DataGasCost(byte[] inputData);

        byte[] Run(byte[] inputData);
    }
}