using System.Numerics;
using Nevermind.Core;

namespace Nevermind.Evm
{
    public class ExecutionEnvironment
    {
        public Address ExecutingAccount { get; set; }

        public Address Originator { get; set; }

        public Address Sender { get; set; }

        public BigInteger GasPrice { get; set; }

        public byte[] InputData { get; set; }

        public BigInteger TransferValue { get; set; }

        public BigInteger Value { get; set; }

        public byte[] MachineCode { get; set; }

        public BlockHeader CurrentBlock { get; set; }

        public int CallDepth { get; set; }
    }
}