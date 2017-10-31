using System;
using System.Numerics;

namespace Nevermind.Evm
{
    public class EvmState
    {
        private BigInteger _activeWordsInMemory;

        public EvmState(BigInteger gasAvailable, EvmStack stack = null)
        {
            GasAvailable = gasAvailable;
            Stack = stack ?? new EvmStack();
        }

        public BigInteger GasAvailable { get; set; }
        public BigInteger ProgramCounter { get; set; }

        public BigInteger ActiveWordsInMemory
        {
            get => _activeWordsInMemory;
            set
            {
                if (value != _activeWordsInMemory && ShouldLog.VM)
                {
                    Console.WriteLine($"  MEMORY SIZE CHANGED FROM {_activeWordsInMemory} TO {value}");
                }
                else if (ShouldLog.VM)
                {
                    Console.WriteLine($"  MEMORY SIZE REMAINS {_activeWordsInMemory}");
                }

                _activeWordsInMemory = value;
            }
        }

        public EvmMemory Memory { get; } = new EvmMemory();
        public EvmStack Stack { get; }
    }
}