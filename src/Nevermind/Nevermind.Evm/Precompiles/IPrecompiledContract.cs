using System.Numerics;

namespace Nevermind.Evm.Precompiles
{
    public interface IPrecompiledContract
    {
        BigInteger Address { get; }

        long BaseGasCost();

        long DataGasCost(byte[] inputData);

        byte[] Run(byte[] inputData);
    }
}