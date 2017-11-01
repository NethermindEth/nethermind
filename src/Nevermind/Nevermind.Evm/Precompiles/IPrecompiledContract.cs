using System.Numerics;

namespace Nevermind.Evm.Precompiles
{
    public interface IPrecompiledContract
    {
        BigInteger Address { get; }

        ulong GasCost(byte[] inputData);

        byte[] Run(byte[] inputData);
    }
}