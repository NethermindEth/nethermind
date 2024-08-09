// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.TxPool;

namespace EngineRequestsGenerator.TestCases;

public static class SStoreWithState
{
    public static void DeployContract(TestCase testCase, ITxPool txPool, long previousBlockNumber, PrivateKey privateKey, long blockGasConsumptionTarget)
    {
        int numberOfContractsToDeployInEachBlock = 2;

        foreach (Transaction tx in GetTxs(testCase, privateKey, previousBlockNumber, numberOfContractsToDeployInEachBlock, blockGasConsumptionTarget))
        {
            txPool.SubmitTx(tx, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
        }
    }

    private static Transaction[] GetTxs(TestCase testCase, PrivateKey privateKey, long previousBlockNumber, int numberOfContractsPerBlock, long blockGasConsumptionTarget)
    {
        bool deployCallingContract = previousBlockNumber == 1;
        int currentNonce = (int)(previousBlockNumber - 1) * numberOfContractsPerBlock + 1;
        int numberOfContractsInThisBlock = numberOfContractsPerBlock + (deployCallingContract ? 1 : 0);

        Transaction[] txs = new Transaction[numberOfContractsInThisBlock];

        if (deployCallingContract)
        {
            // deploying contract calling contracts with actual test cases
            txs[0] = Build.A.Transaction
                .WithNonce(UInt256.Zero)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(1.GWei())
                .WithMaxPriorityFeePerGas(1.GWei())
                .WithTo(null)
                .WithChainId(BlockchainIds.Holesky)
                .WithData(PrepareContractCallCode(privateKey.Address, numberOfContractsPerBlock, blockGasConsumptionTarget))
                .WithGasLimit(blockGasConsumptionTarget)
                .SignedAndResolved(privateKey)
                .TestObject;
        }

        for (int i = 0; i < numberOfContractsPerBlock; i++)
        {
            // deploing contracts with sstore instructions
            txs[i + (deployCallingContract ? 1 : 0)] = Build.A.Transaction
                .WithNonce((UInt256)(currentNonce + i))
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(1.GWei())
                .WithMaxPriorityFeePerGas(1.GWei())
                .WithTo(null)
                .WithChainId(BlockchainIds.Holesky)
                .WithData(PrepareCode(testCase, currentNonce - 1 + i))
                .WithGasLimit(blockGasConsumptionTarget)
                .SignedAndResolved(privateKey)
                .TestObject;
        }

        return txs;
    }

    private static byte[] PrepareContractCallCode(Address senderAddress, int numberOfContractsPerBlock, long blockGasConsumptionTarget)
    {
        int numberOfContractsToCall = (int)(blockGasConsumptionTarget / 1_000_000 - 1) * numberOfContractsPerBlock;
        byte[] gasTarget = blockGasConsumptionTarget.ToBigEndianByteArrayWithoutLeadingZeros();

        List<byte> codeToDeploy = new();

        for (int i = 0; i < numberOfContractsToCall; i++)
        {
            Address contractAddress = ContractAddress.From(ContractAddress.From(senderAddress, (UInt256)i + 1), 1);

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

            codeToDeploy.Add((byte)Instruction.PUSH0);                                          // zero

            switch (testCase)
            {
                case TestCase.SStoreManyAccountsConsecutiveKeysZeroValue:
                    codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)storageKey.Length - 1));  // storage key
                    codeToDeploy.AddRange(storageKey);
                    break;
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
