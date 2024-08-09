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

public static class SStore
{
    public static Transaction[] GetTxs(TestCase testCase, PrivateKey privateKey, int nonce, long blockGasConsumptionTarget)
    {
        int numberOfContractsToDeploy = (int)(blockGasConsumptionTarget / 6_000_000); // more or less cost of deploying 1 contract

        Transaction[] txs = new Transaction[numberOfContractsToDeploy + 1];

        // deploying contract calling contracts with actual test cases
        txs[0] = Build.A.Transaction
            .WithNonce((UInt256)nonce)
            .WithType(TxType.EIP1559)
            .WithMaxFeePerGas(1.GWei())
            .WithMaxPriorityFeePerGas(1.GWei())
            .WithTo(null)
            .WithChainId(BlockchainIds.Holesky)
            .WithData(PrepareContractCallCode(privateKey.Address, (UInt256)nonce + 1, numberOfContractsToDeploy, blockGasConsumptionTarget))
            .WithGasLimit(blockGasConsumptionTarget)
            .SignedAndResolved(privateKey)
            .TestObject;

        for (int i = 0; i < numberOfContractsToDeploy; i++)
        {
            // deploing contracts with sstore instructions
            txs[i + 1] = Build.A.Transaction
                .WithNonce((UInt256)(nonce + 1 + i))
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(1.GWei())
                .WithMaxPriorityFeePerGas(1.GWei())
                .WithTo(null)
                .WithChainId(BlockchainIds.Holesky)
                .WithData(PrepareCode(testCase, i))
                .WithGasLimit(blockGasConsumptionTarget)
                .SignedAndResolved(privateKey)
                .TestObject;
        }

        return txs;
    }

    private static byte[] PrepareContractCallCode(Address senderAddress, UInt256 firstNonce, int numberOfContractsToDeploy, long blockGasConsumptionTarget)
    {
        byte[] gasTarget = blockGasConsumptionTarget.ToBigEndianByteArrayWithoutLeadingZeros();

        List<byte> codeToDeploy = new();

        for (int i = 0; i < numberOfContractsToDeploy; i++)
        {
            Address contractAddress = ContractAddress.From(ContractAddress.From(senderAddress, firstNonce + (UInt256)i), 1);

            codeToDeploy.Add((byte)Instruction.PUSH0);
            codeToDeploy.Add((byte)Instruction.PUSH0);
            codeToDeploy.Add((byte)Instruction.PUSH0);
            codeToDeploy.Add((byte)Instruction.PUSH0);
            codeToDeploy.Add((byte)Instruction.PUSH0);
            codeToDeploy.Add((byte)Instruction.PUSH20);
            codeToDeploy.AddRange(contractAddress.Bytes);
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)gasTarget.Length - 1));
            codeToDeploy.AddRange(gasTarget);
            codeToDeploy.Add((byte)Instruction.CALL);
            codeToDeploy.Add((byte)Instruction.POP);
        }

        List<byte> byteCode = ContractFactory.GenerateCodeToDeployContract(codeToDeploy);
        return byteCode.ToArray();
    }

    private static byte[] PrepareCode(TestCase testCase, int offset)
    {
        int iterations = 350;
        int startingPoint = offset * iterations;

        List<byte> codeToDeploy = new();

        for (int i = startingPoint; i < startingPoint + iterations; i++)
        {
            byte[] storageKey = ((long)i).ToBigEndianByteArrayWithoutLeadingZeros();

            switch (testCase)
            {
                case TestCase.SStoreManyAccountsConsecutiveKeysZeroValue:
                case TestCase.SStoreManyAccountsRandomKeysZeroValue:
                    codeToDeploy.Add((byte)Instruction.PUSH1);                                 // zero
                    codeToDeploy.Add(0x00);
                    break;
                default:
                    codeToDeploy.Add((byte)Instruction.PUSH32);                                 // random value
                    codeToDeploy.AddRange(Keccak.Compute((i + 100_000).ToByteArray()).Bytes);
                    break;
            }


            switch (testCase)
            {
                case TestCase.SStoreManyAccountsConsecutiveKeysRandomValue:
                case TestCase.SStoreManyAccountsConsecutiveKeysZeroValue:
                    codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)storageKey.Length - 1));  // storage key
                    codeToDeploy.AddRange(storageKey);
                    break;
                case TestCase.SStoreManyAccountsRandomKeysRandomValue:
                case TestCase.SStoreManyAccountsRandomKeysZeroValue:
                    codeToDeploy.Add((byte)Instruction.PUSH32);                                 // storage key
                    codeToDeploy.AddRange(Keccak.Compute(i.ToByteArray()).Bytes);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(testCase), testCase, null);
            }

            codeToDeploy.Add((byte)Instruction.SSTORE);
        }

        List<byte> byteCode = ContractFactory.GenerateCodeToDeployContract(codeToDeploy);
        return byteCode.ToArray();
    }
}
