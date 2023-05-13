// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        public void Calculates_RequestId_Correctly_No_Signature()
        {
            //Using the following transaction as reference: https://goerli.etherscan.io/tx/0xa9236155292e30bfb43c5a758e0c906e18697cf23198f81e2a72e5322cd0acb7#eventlog
            UserOperation userOperation = new(new UserOperationRpc
            {
                Sender = new Address("0x05c022028ef3e2c61b3babe0fbc8f658bc4b431f"),
                Nonce = 5,
                CallData =
                    Bytes.FromHexString(
                        "0x2b311337000000000000000000000000000000000000000000000000000000000000012c"),
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
            userOperation.CalculateRequestId(entryPointId, chainId);
            Assert.That(userOperation.RequestId!, Is.EqualTo(idFromTransaction),
                "Request IDs do not match.");
        }

        [Test]
        public void Calculates_RequestId_Correctly_With_Signature()
        {
            UserOperation userOperation2 = new(new UserOperationRpc
            {
                Sender = new Address("0x65f1326ef62E7b63B2EdF41840E37eB2a0F97515"),
                Nonce = 7,
                CallData =
                    Bytes.FromHexString(
                        "0x80c5c7d000000000000000000000000017e4493e5dc3e0bafdb68147cf15f52f669ef91d000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000600000000000000000000000000000000000000000000000000000000000000004278ddd3c00000000000000000000000000000000000000000000000000000000"),
                InitCode = Bytes.Empty,
                CallGas = 29129,
                VerificationGas = 100000,
                PreVerificationGas = 21000,
                MaxFeePerGas = 1000000007,
                MaxPriorityFeePerGas = 1000000000,
                Paymaster = new Address("0x0000000000000000000000000000000000000000"),
                PaymasterData = Bytes.Empty,
                Signature = Bytes.FromHexString(
                    "0xe4ef96c1ebffdae061838b79a0ba2b0289083099dc4d576a7ed0c61c80ed893273ba806a581c72be9e550611defe0bf490f198061b8aa63dd6acfc0b620e0c871c")
            });


            Address entryPointId = new Address("0x90f3e1105e63c877bf9587de5388c23cdb702c6b");
            ulong chainId = 5;
            Keccak idFromTransaction2 =
                new Keccak("0x87c3605deda77b02b78e62157309985d94531cf7fbb13992c602c8555bece921");
            userOperation2.CalculateRequestId(entryPointId, chainId);
            Assert.That(userOperation2.RequestId!, Is.EqualTo(idFromTransaction2),
                "Request IDs do not match.");
        }
    }
}

