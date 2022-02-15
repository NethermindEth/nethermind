using Nethermind.AccountAbstraction.Data;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.AccountAbstraction.Test.Network
{
    [TestFixture]
    public class CalculateRequestIdTest
    {
        [Test]
        public void Calculates_RequestId_Correctly()
        {
            //Using the following transaction as reference: https://goerli.etherscan.io/tx/0xa9236155292e30bfb43c5a758e0c906e18697cf23198f81e2a72e5322cd0acb7#eventlog
            UserOperation userOperation = new(new UserOperationRpc
            {
                Sender = new Address("0x05c022028ef3e2c61b3babe0fbc8f658bc4b431f"),
                Nonce = 5,
                CallData = Bytes.FromHexString("0x2b311337000000000000000000000000000000000000000000000000000000000000012c"),
                InitCode = Bytes.Empty,
                CallGas = 21000,
                VerificationGas = 21000,
                PreVerificationGas = 21000,
                MaxFeePerGas = 2100000000,
                MaxPriorityFeePerGas = 2100000000,
                Paymaster = new Address("0x0000000000000000000000000000000000000000"),
                PaymasterData = Bytes.Empty,
                Signature = Bytes.Empty
            });

            Address entryPointId = new Address("0x90f3E1105E63C877bF9587DE5388C23Cdb702c6B");
            ulong chainId = 5;
            Keccak idFromTransaction = new Keccak("0x9f5d37eb5cc7b0707b2898b1da01fa7aac806a18d531b17a981994bc512cbfc8");
            Keccak idFromUserOperation = userOperation.CalculateRequestId(entryPointId, chainId);
            Assert.AreEqual(idFromUserOperation, idFromTransaction,
                "Request IDs do not match.");
        }
    }
}
