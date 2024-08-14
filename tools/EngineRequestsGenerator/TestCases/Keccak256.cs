// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;

namespace EngineRequestsGenerator.TestCases;

public static class Keccak256
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
            .WithData(PrepareKeccak256Code(testCase))
            .WithGasLimit(blockGasConsumptionTarget)
            .SignedAndResolved(privateKey)
            .TestObject
        ];

    }

    private static byte[] PrepareKeccak256Code(TestCase testCase)
    {
        int bytesToComputeKeccak = testCase switch
        {
            TestCase.Keccak256From1Byte => 1,
            TestCase.Keccak256From8Bytes => 8,
            TestCase.Keccak256From32Bytes => 32,
            _ => throw new ArgumentOutOfRangeException(nameof(testCase), testCase, null)
        };

        List<byte> oneIteration = new();
        // long costOfOneIteration = 0;

        oneIteration.Add((byte)Instruction.PUSH0);
        // costOfOneIteration += GasCostOf.Base;
        oneIteration.Add((byte)Instruction.MSTORE);
        // costOfOneIteration += GasCostOf.Memory;
        oneIteration.Add((byte)Instruction.PUSH1);
        oneIteration.Add((byte)bytesToComputeKeccak);
        // costOfOneIteration += GasCostOf.VeryLow;
        oneIteration.Add((byte)Instruction.PUSH0);
        // costOfOneIteration += GasCostOf.Base;
        oneIteration.Add((byte)Instruction.KECCAK256);
        // costOfOneIteration += GasCostOf.Call;

        List<byte> codeToDeploy = new();
        // long cost = 0;

        codeToDeploy.Add((byte)Instruction.CALLER);     // first, preitaration item - put on stack callers address
        // cost += GasCostOf.Base;
        codeToDeploy.Add((byte)Instruction.JUMPDEST);   // second item - jump destination (on offset 1)
        // cost += GasCostOf.JumpDest;

        for (int i = 0; i < 4095; i++)
        {
            codeToDeploy.AddRange(oneIteration);
            // cost += costOfOneIteration;
        }

        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)1);
        // cost += GasCostOf.VeryLow;
        codeToDeploy.Add((byte)Instruction.JUMP);
        // cost += GasCostOf.Mid;

        List<byte> byteCode = ContractFactory.GenerateCodeToDeployContract(codeToDeploy);
        return byteCode.ToArray();
    }
}
