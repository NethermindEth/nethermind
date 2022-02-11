using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Network;
using Nethermind.Core;
using Nethermind.Network;
using System.Collections.Generic;
using System.Net;

namespace Nethermind.AccountAbstraction.Test.Network
{
    [TestFixture]
    public class CalculateRequestIdTest
    {
        UserOperation userOperation = new(new UserOperationRpc
        {
            Sender = new Address(1.ToString("x40")),
            Nonce = 1000,
            CallData = new byte[] { 1, 2 },
            InitCode = new byte[] { 3, 4 },
            CallGas = 5,
            VerificationGas = 6,
            PreVerificationGas = 7,
            MaxFeePerGas = 8,
            MaxPriorityFeePerGas = 1,
            Paymaster = new Address(2.ToString("x40")),
            PaymasterData = new byte[] { 5, 6 },
            Signature = new byte[] { 1, 2, 3 }
        });

        string IdFromEntryPoint = ;
        Keccak IdFromUserOperationCs = userOperation.RequestId;

        IdFromEntryPoint.ShouldBe(IdFromUserOperationCs);

    }

}