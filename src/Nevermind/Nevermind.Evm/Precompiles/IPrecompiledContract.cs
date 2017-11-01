using System.Numerics;

namespace Nevermind.Evm.Precompiles
{
    public interface IPrecompiledContract
    {
        BigInteger Address { get; }

        BigInteger GasCost(byte[] inputData);

        byte[] Run(byte[] inputData);
    }
}