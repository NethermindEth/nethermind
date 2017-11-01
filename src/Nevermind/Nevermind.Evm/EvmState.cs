using System;
using System.Numerics;

namespace Nevermind.Evm
{
    public class EvmState
    {
        private ulong _activeWordsInMemory;

        public EvmState(ulong gasAvailable)
        {
            GasAvailable = gasAvailable;
        }

        public ulong GasAvailable { get; set; }
        public BigInteger ProgramCounter { get; set; }

        public ulong ActiveWordsInMemory
        {
            get => _activeWordsInMemory;
            set
            {
                if (value != _activeWordsInMemory && ShouldLog.Evm)
                {
                    Console.WriteLine($"  MEMORY SIZE CHANGED FROM {_activeWordsInMemory} TO {value}");
                }
                else if (ShouldLog.Evm)
                {
                    Console.WriteLine($"  MEMORY SIZE REMAINS {_activeWordsInMemory}");
                }

                _activeWordsInMemory = value;
            }
        }

        public EvmMemory Memory { get; } = new EvmMemory();
        public EvmStack Stack { get; } = new EvmStack();
    }
}