﻿// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;

namespace EngineRequestsGenerator.TestCases;

public static class PointEvaluation
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
                .WithData(PrepareCode(testCase, blockGasConsumptionTarget))
                .WithGasLimit(blockGasConsumptionTarget)
                .SignedAndResolved(privateKey)
                .TestObject
        ];
    }

    private static byte[] PrepareCode(TestCase testCase, long blockGasConsumptionTarget)
    {
        switch (testCase)
        {
            case TestCase.PointEvaluationOneData:
                return PrepareCodeOneData().Result;
            default:
                throw new ArgumentOutOfRangeException(nameof(testCase), testCase, null);
        }
    }

    private static byte[] PrepareCodeZeros()
    {
        List<byte> codeToDeploy = new();

        byte[] gasTarget = ((long)50000).ToBigEndianByteArrayWithoutLeadingZeros();

        codeToDeploy.Add((byte)Instruction.PUSH0);                     // versioned hashes
        codeToDeploy.Add((byte)Instruction.PUSH0);
        codeToDeploy.Add((byte)Instruction.MSTORE);

        codeToDeploy.Add((byte)Instruction.PUSH0);                     // x
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)32);
        codeToDeploy.Add((byte)Instruction.MSTORE);

        codeToDeploy.Add((byte)Instruction.PUSH0);                     // y
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)64);
        codeToDeploy.Add((byte)Instruction.MSTORE);

        codeToDeploy.Add((byte)Instruction.PUSH0);                     // commitment and proof
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)96);
        codeToDeploy.Add((byte)Instruction.MSTORE);
        codeToDeploy.Add((byte)Instruction.PUSH0);
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)128);
        codeToDeploy.Add((byte)Instruction.MSTORE);
        codeToDeploy.Add((byte)Instruction.PUSH0);
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)160);
        codeToDeploy.Add((byte)Instruction.MSTORE);

        int jumpDestPosition = codeToDeploy.Count;
        codeToDeploy.Add((byte)Instruction.JUMPDEST);

        for (int i = 0; i < 1; i++)
        {
            codeToDeploy.Add((byte)Instruction.PUSH1);  // return size
            codeToDeploy.Add((byte)64);
            codeToDeploy.Add((byte)Instruction.PUSH1);  // return offset
            codeToDeploy.Add((byte)192);
            codeToDeploy.Add((byte)Instruction.PUSH1);  // args size
            codeToDeploy.Add((byte)192);
            codeToDeploy.Add((byte)Instruction.PUSH0);  // args offset
            codeToDeploy.Add((byte)Instruction.PUSH1);  // address
            codeToDeploy.Add((byte)10);
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)gasTarget.Length - 1));
            codeToDeploy.AddRange(gasTarget);
            codeToDeploy.Add((byte)Instruction.STATICCALL);
            codeToDeploy.Add((byte)Instruction.POP);
        }

        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)jumpDestPosition);
        codeToDeploy.Add((byte)Instruction.JUMP);

        List<byte> byteCode = ContractFactory.GenerateCodeToDeployContract(codeToDeploy);
        return byteCode.ToArray();
    }

    private static async Task<byte[]> PrepareCodeOneData()
    {
        await KzgPolynomialCommitments.InitializeAsync();

        List<byte> codeToDeploy = new();

        byte[] gasTarget = ((long)50000).ToBigEndianByteArrayWithoutLeadingZeros();

        Transaction tx = Build.A.Transaction
            .WithNonce(UInt256.Zero)
            .WithMaxFeePerGas(1.GWei())
            .WithMaxPriorityFeePerGas(1.GWei())
            .WithChainId(BlockchainIds.Holesky)
            .WithShardBlobTxTypeAndFields()
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        codeToDeploy.Add((byte)Instruction.PUSH32);                    // versioned hashes
        codeToDeploy.AddRange(tx.BlobVersionedHashes.FirstOrDefault());
        codeToDeploy.Add((byte)Instruction.PUSH0);
        codeToDeploy.Add((byte)Instruction.MSTORE);

        codeToDeploy.Add((byte)Instruction.PUSH0);                     // x
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)32);
        codeToDeploy.Add((byte)Instruction.MSTORE);

        codeToDeploy.Add((byte)Instruction.PUSH0);                     // y
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)64);
        codeToDeploy.Add((byte)Instruction.MSTORE);

        List<byte> commitmentAndProof = new();
        commitmentAndProof.AddRange(((ShardBlobNetworkWrapper)tx.NetworkWrapper).Commitments.FirstOrDefault());
        commitmentAndProof.AddRange(((ShardBlobNetworkWrapper)tx.NetworkWrapper).Proofs.FirstOrDefault());

        codeToDeploy.Add((byte)Instruction.PUSH32);                     // commitment and proof
        codeToDeploy.AddRange(commitmentAndProof.Slice(0, 32));
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)96);
        codeToDeploy.Add((byte)Instruction.MSTORE);
        codeToDeploy.Add((byte)Instruction.PUSH32);
        codeToDeploy.AddRange(commitmentAndProof.Slice(32, 32));
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)128);
        codeToDeploy.Add((byte)Instruction.MSTORE);
        codeToDeploy.Add((byte)Instruction.PUSH32);
        codeToDeploy.AddRange(commitmentAndProof.Slice(64, 32));
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)160);
        codeToDeploy.Add((byte)Instruction.MSTORE);

        int jumpDestPosition = codeToDeploy.Count;
        codeToDeploy.Add((byte)Instruction.JUMPDEST);

        for (int i = 0; i < 1000; i++)
        {
            codeToDeploy.Add((byte)Instruction.PUSH1);  // return size
            codeToDeploy.Add((byte)64);
            codeToDeploy.Add((byte)Instruction.PUSH1);  // return offset
            codeToDeploy.Add((byte)192);
            codeToDeploy.Add((byte)Instruction.PUSH1);  // args size
            codeToDeploy.Add((byte)192);
            codeToDeploy.Add((byte)Instruction.PUSH0);  // args offset
            codeToDeploy.Add((byte)Instruction.PUSH1);  // address
            codeToDeploy.Add((byte)10);
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)gasTarget.Length - 1));
            codeToDeploy.AddRange(gasTarget);
            codeToDeploy.Add((byte)Instruction.STATICCALL);
            codeToDeploy.Add((byte)Instruction.POP);
        }

        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)jumpDestPosition);
        codeToDeploy.Add((byte)Instruction.JUMP);

        List<byte> byteCode = ContractFactory.GenerateCodeToDeployContract(codeToDeploy);
        return byteCode.ToArray();
    }
}
