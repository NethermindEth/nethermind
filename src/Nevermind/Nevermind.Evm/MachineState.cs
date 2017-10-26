using System;
using System.Numerics;

namespace Nevermind.Evm
{
    public class MachineState
    {
        private const bool IsLogging = false;

        public MachineState(BigInteger gasAvailable)
        {
            GasAvailable = gasAvailable;
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
                if (IsLogging)
                {
                    Console.WriteLine($"MEMORY SIZE CHANGED TO {value}");
                }
            }
        }
        public EvmStack Stack { get; set; } = new EvmStack();
    }
}