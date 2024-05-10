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

public static class SStoreOneKey
{
    public static Transaction[] GetTxs(TestCase testCase, PrivateKey privateKey, int nonce, long blockGasConsumptionTarget)
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
                .WithData(PrepareCode(testCase))
                .WithGasLimit(blockGasConsumptionTarget)
                .SignedAndResolved(privateKey)
                .TestObject
        ];
    }

    private static byte[] PrepareCode(TestCase testCase)
    {
        switch (testCase)
        {
            case TestCase.SStoreOneAccountOneKeyTwoValues:
                return PrepareCodeTwoValues(testCase).ToArray();
            default:
                return PrepareCodeOneValue(testCase).ToArray();
        }
    }

    private static byte[] PrepareCodeOneValue(TestCase testCase)
    {
        byte[] constantWord = Keccak.Compute("random").BytesToArray();

        List<byte> codeToDeploy = new();

        codeToDeploy.Add((byte)Instruction.JUMPDEST);

        for (int i = 0; i < 500; i++)
        {
            switch (testCase)
            {
                case TestCase.SStoreOneAccountOneKeyZeroValue:
                    codeToDeploy.Add((byte)Instruction.PUSH0);              // zero value
                    break;
                case TestCase.SStoreOneAccountOneKeyConstantValue:
                    codeToDeploy.Add((byte)Instruction.PUSH32);             // constant value
                    codeToDeploy.AddRange(constantWord);
                    break;
                case TestCase.SStoreOneAccountOneKeyRandomValue:
                    codeToDeploy.Add((byte)Instruction.PUSH32);             // random value
                    codeToDeploy.AddRange(Keccak.Compute(i.ToByteArray()).Bytes);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(testCase), testCase, null);
            }

            codeToDeploy.Add((byte)Instruction.PUSH0);                      // key
            codeToDeploy.Add((byte)Instruction.SSTORE);
        }

        codeToDeploy.Add((byte)Instruction.PUSH0);
        codeToDeploy.Add((byte)Instruction.JUMP);

        List<byte> byteCode = ContractFactory.GenerateCodeToDeployContract(codeToDeploy);
        return byteCode.ToArray();
    }

    private static byte[] PrepareCodeTwoValues(TestCase testCase)
    {
        byte[] constantWord = Keccak.Compute("random").BytesToArray();

        List<byte> codeToDeploy = new();

        codeToDeploy.Add((byte)Instruction.JUMPDEST);

        for (int i = 0; i < 500; i++)
        {
            codeToDeploy.Add((byte)Instruction.PUSH0);              // zero value
            codeToDeploy.Add((byte)Instruction.PUSH0);              // key
            codeToDeploy.Add((byte)Instruction.SSTORE);
            codeToDeploy.Add((byte)Instruction.PUSH32);             // constant value
            codeToDeploy.AddRange(Keccak.Compute(i.ToByteArray()).Bytes);
            codeToDeploy.Add((byte)Instruction.PUSH0);              // same key
            codeToDeploy.Add((byte)Instruction.SSTORE);
        }

        codeToDeploy.Add((byte)Instruction.PUSH0);
        codeToDeploy.Add((byte)Instruction.JUMP);

        List<byte> byteCode = ContractFactory.GenerateCodeToDeployContract(codeToDeploy);
        return byteCode.ToArray();
    }
}
