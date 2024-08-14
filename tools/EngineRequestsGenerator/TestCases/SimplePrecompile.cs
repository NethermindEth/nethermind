// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;

namespace EngineRequestsGenerator.TestCases;

public static class SimplePrecompile
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
        long byteSizeOfArgs = testCase switch
        {
            TestCase.SHA2From1Byte => 1,
            TestCase.SHA2From8Bytes => 8,
            TestCase.SHA2From32Bytes => 32,
            TestCase.SHA2From128Bytes => 128,
            TestCase.SHA2From1024Bytes => 1024,
            TestCase.SHA2From16KBytes => 16_384,
            TestCase.RipemdFrom1Byte => 1,
            TestCase.RipemdFrom8Bytes => 8,
            TestCase.RipemdFrom32Bytes => 32,
            TestCase.RipemdFrom128Bytes => 128,
            TestCase.RipemdFrom1024Bytes => 1024,
            TestCase.RipemdFrom16KBytes => 16_384,
            TestCase.IdentityFrom1Byte => 1,
            TestCase.IdentityFrom8Bytes => 8,
            TestCase.IdentityFrom32Bytes => 32,
            TestCase.IdentityFrom128Bytes => 128,
            TestCase.IdentityFrom1024Bytes => 1024,
            TestCase.IdentityFrom16KBytes => 16_384,
            _ => throw new ArgumentOutOfRangeException(nameof(testCase), testCase, null)
        };

        List<byte> codeToDeploy = new();

        byte[] gasTarget = blockGasConsumptionTarget.ToBigEndianByteArrayWithoutLeadingZeros();

        for (long i = 0; i < byteSizeOfArgs; i += 32)
        {
            codeToDeploy.Add((byte)Instruction.PUSH32);
            codeToDeploy.AddRange(Keccak.Compute(i.ToBigEndianByteArrayWithoutLeadingZeros()).Bytes);

            if (i == 0)
            {
                codeToDeploy.Add((byte)Instruction.PUSH0);
            }
            else
            {
                byte[] offset = i.ToBigEndianByteArrayWithoutLeadingZeros();

                codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)offset.Length - 1));
                codeToDeploy.AddRange(offset);
            }

            codeToDeploy.Add((byte)Instruction.MSTORE);
        }

        long jumpDestPosition = codeToDeploy.Count;
        byte[] jumpDestBytes = jumpDestPosition.ToBigEndianByteArrayWithoutLeadingZeros();
        codeToDeploy.Add((byte)Instruction.JUMPDEST);

        for (int i = 0; i < 100; i++)
        {
            byte[] bytesOfArgs = byteSizeOfArgs.ToBigEndianByteArrayWithoutLeadingZeros();

            codeToDeploy.Add((byte)Instruction.PUSH1);                                      // return size
            codeToDeploy.Add(0x20);
            codeToDeploy.Add((byte)Instruction.PUSH0);                                      // return offset
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)bytesOfArgs.Length - 1));     // args size
            codeToDeploy.AddRange(bytesOfArgs);
            codeToDeploy.Add((byte)Instruction.PUSH0);                                      // args offset
            codeToDeploy.Add((byte)Instruction.PUSH1);                                      // address
            codeToDeploy.Add(precompileAddress);
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)gasTarget.Length - 1));
            codeToDeploy.AddRange(gasTarget);
            codeToDeploy.Add((byte)Instruction.STATICCALL);
            codeToDeploy.Add((byte)Instruction.POP);
        }

        codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)jumpDestBytes.Length - 1));
        codeToDeploy.AddRange(jumpDestBytes);
        codeToDeploy.Add((byte)Instruction.JUMP);

        List<byte> byteCode = ContractFactory.GenerateCodeToDeployContract(codeToDeploy);
        return byteCode.ToArray();
    }
}
