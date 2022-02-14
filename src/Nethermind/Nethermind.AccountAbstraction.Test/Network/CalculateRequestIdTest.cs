using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Network;
using Nethermind.Core;
using Nethermind.Network;
using System.Collections.Generic;
using System.Net;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.AccountAbstraction.Test.Network
{
    [TestFixture]
    public class CalculateRequestIdTest
    {
        [Test]
        private void CompareRequestIds()
        {
            //Using the following transaction as reference: https://goerli.etherscan.io/tx/0xa9236155292e30bfb43c5a758e0c906e18697cf23198f81e2a72e5322cd0acb7#eventlog
            UserOperation userOperation = new(new UserOperationRpc
            {
                Sender =
                    new Address(1.ToString("0x05c022028ef3e2c61b3babe0fbc8f658bc4b431f")),
                Nonce = 5,
                CallData = "0x2b31133700000000000000000000000000000000000000000000000000...00012c" //new byte[] { },
                InitCode = "0x" // new byte[] { },
                CallGas = 21000,
                VerificationGas = 21000,
                PreVerificationGas = 21000,
                MaxFeePerGas = 2100000000,
                MaxPriorityFeePerGas = 2100000000,
                Paymaster = new Address(2.ToString("x0000000000000000000000000000000000000000")),
                PaymasterData = "0x" // new byte[] { },
                Signature = "0x" //new byte[] { }
            });

            string IdFromTransaction = "0x9f5d37eb5cc7b0707b2898b1da01fa7aac806a18d531b17a981994bc512cbfc8";

            Keccak IdFromUserOperation = userOperation.RequestId;
            Assert.AreEqual(IdFromUserOperation.ToString(true), IdFromTransaction,
                "Request IDs do not match.")
            //IdFromTransaction.ShouldBe(IdFromUserOperation)??
        }
    }
}
