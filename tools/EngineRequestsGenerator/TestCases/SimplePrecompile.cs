// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;

namespace EngineRequestsGenerator.TestCases;

public class SimplePrecompile
{
    public static Transaction[] GetTxs(byte precompileAddress, TestCase testcase, PrivateKey privateKey, int nonce, long blockGasConsumptionTarget)
    {
        return
        [
            Build.A.Transaction
            .WithNonce((UInt256)nonce)
            .WithType(TxType.EIP1559)
            .WithMaxFeePerGas(1.GWei())
            .WithMaxPriorityFeePerGas(1.GWei())
            .WithTo(null)
            .WithChainId(BlockchainIds.Holesky)
            .WithData(PrepareCode(precompileAddress, testcase, blockGasConsumptionTarget))
            .WithGasLimit(blockGasConsumptionTarget)
            .SignedAndResolved(privateKey)
            .TestObject
        ];
    }

    private static byte[] PrepareCode(byte precompileAddress, TestCase testCase, long blockGasConsumptionTarget)
    {
        int bytesToCalcs = testCase switch
        {
            TestCase.SHA2From1Byte => 1,
            TestCase.SHA2From8Bytes => 8,
            TestCase.SHA2From32Bytes => 32,
            TestCase.SHA2From128Bytes => 128,
            TestCase.RipemdFrom1Byte => 1,
            TestCase.RipemdFrom8Bytes => 8,
            TestCase.RipemdFrom32Bytes => 32,
            TestCase.RipemdFrom128Bytes => 128,
            TestCase.IdentityFrom1Byte => 1,
            TestCase.IdentityFrom8Bytes => 8,
            TestCase.IdentityFrom32Bytes => 32,
            TestCase.IdentityFrom128Bytes => 128,
            _ => throw new ArgumentOutOfRangeException(nameof(testCase), testCase, null)
        };

        List<byte> codeToDeploy = new();

        byte[] gasTarget = blockGasConsumptionTarget.ToBigEndianByteArrayWithoutLeadingZeros();

        codeToDeploy.Add((byte)Instruction.CALLER);
        codeToDeploy.Add((byte)Instruction.PUSH0);
        codeToDeploy.Add((byte)Instruction.MSTORE);

        codeToDeploy.Add((byte)Instruction.JUMPDEST);

        for (int i = 0; i < 1000; i++)
        {
            codeToDeploy.Add((byte)Instruction.PUSH1);  // return size
            codeToDeploy.Add(0x20);
            codeToDeploy.Add((byte)Instruction.PUSH1);  // return offset
            codeToDeploy.Add(0x20);
            codeToDeploy.Add((byte)Instruction.PUSH1);  // args size
            codeToDeploy.Add((byte)bytesToCalcs);
            codeToDeploy.Add((byte)Instruction.PUSH1);  // args offset
            codeToDeploy.Add(0x20);
            codeToDeploy.Add((byte)Instruction.PUSH1);  // address
            codeToDeploy.Add(precompileAddress);
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)gasTarget.Length - 1));
            codeToDeploy.AddRange(gasTarget);
            codeToDeploy.Add((byte)Instruction.STATICCALL);
            codeToDeploy.Add((byte)Instruction.POP);
        }

        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add(0x03);
        codeToDeploy.Add((byte)Instruction.JUMP);

        List<byte> byteCode = ContractFactory.GenerateCodeToDeployContract(codeToDeploy);
        return byteCode.ToArray();
    }
}
