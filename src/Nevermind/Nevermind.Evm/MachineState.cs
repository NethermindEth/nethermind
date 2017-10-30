using System;
using System.Numerics;

namespace Nevermind.Evm
{
    public class MachineState
    {
        public MachineState(BigInteger gasAvailable, int callDepth = 0)
        {
            GasAvailable = gasAvailable;
            Stack = new EvmStack(callDepth);
        }

        public BigInteger GasAvailable { get; set; }
        public BigInteger ProgramCounter { get; set; }
        public EvmMemory Memory { get; set; } = new EvmMemory();
        private BigInteger _activeWordsInMemory;
        public BigInteger ActiveWordsInMemory
        {
            get => _activeWordsInMemory;
            set
            {
                _activeWordsInMemory = value;
                if (ShouldLog.VM)
                {
                    Console.WriteLine($"MEMORY SIZE CHANGED TO {value}");
                }
            }
        }

        public EvmStack Stack { get; set; }
    }
}