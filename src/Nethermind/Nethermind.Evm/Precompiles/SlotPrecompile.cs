using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles
{
    public class SlotPrecompile : IPrecompile<SlotPrecompile>
    {
        public static SlotPrecompile Instance { get; } = new();
        
        public static Address Address => new("0x0000000000000000000000000000000000000014");

        private SlotPrecompile() { }

        public long BaseGasCost(IReleaseSpec releaseSpec)
        {
            return 2;
        }

        public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            return 0;
        }

        public (ReadOnlyMemory<byte>, bool) Run(ReadOnlyMemory<byte> inputData, PrecompileContext context)
        {
            ulong? slotNumber = context.BlockExecutionContext.Header.SlotNumber;

            if (slotNumber is null)
            {
                return IPrecompile.Failure;
            }
            
            byte[] result = new byte[32];
            for (int i = 0; i < 8; i++)
            {
                result[31 - i] = (byte)(slotNumber & 0xFF);
                slotNumber >>= 8;
            }

            return (result, true);
        }
    }
} 