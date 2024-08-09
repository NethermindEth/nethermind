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

public static class MStore
{
    public static Transaction[] GetTxs(Instruction instruction, TestCase testCase, PrivateKey privateKey, int nonce, long blockGasConsumptionTarget)
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
                .WithData(PrepareCode(instruction, testCase))
                .WithGasLimit(blockGasConsumptionTarget)
                .SignedAndResolved(privateKey)
                .TestObject
        ];
    }

    private static byte[] PrepareCode(Instruction instruction, TestCase testCase)
    {
        List<byte> codeToDeploy = new();

        codeToDeploy.Add((byte)Instruction.JUMPDEST);

        for (int i = 0; i < 500; i++)
        {
            switch (testCase)
            {
                case TestCase.MStoreZero:
                    codeToDeploy.Add((byte)Instruction.PUSH0);                          // zero value
                    break;
                case TestCase.MStoreRandom:
                    codeToDeploy.Add((byte)Instruction.PUSH32);                         // random value
                    codeToDeploy.AddRange(Keccak.Compute(i.ToByteArray()).Bytes);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(testCase), testCase, null);
            }
            codeToDeploy.Add((byte)Instruction.PUSH0);                                  // offset
            codeToDeploy.Add((byte)instruction);
        }

        codeToDeploy.Add((byte)Instruction.PUSH0);
        codeToDeploy.Add((byte)Instruction.JUMP);

        List<byte> byteCode = ContractFactory.GenerateCodeToDeployContract(codeToDeploy);
        return byteCode.ToArray();
    }
}
