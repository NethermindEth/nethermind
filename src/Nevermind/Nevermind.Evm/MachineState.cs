using System.Numerics;

namespace Nevermind.Evm
{
    public class MachineState
    {
        public MachineState(BigInteger gasAvailable)
        {
            GasAvailable = gasAvailable;
        }

        public BigInteger GasAvailable { get; set; }
        public BigInteger ProgramCounter { get; set; }
        public EvmMemory Memory { get; set; } = new EvmMemory();
        public int ActiveWordsInMemory { get; set; }
        public EvmStack Stack { get; set; } = new EvmStack();
    }
}