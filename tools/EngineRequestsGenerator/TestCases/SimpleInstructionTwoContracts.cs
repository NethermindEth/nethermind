// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;

namespace EngineRequestsGenerator.TestCases;

public static class SimpleInstructionTwoContracts
{
    public static Transaction[] GetTxs(Instruction instruction, PrivateKey privateKey, int nonce, long blockGasConsumptionTarget)
        => GetTxs([instruction], privateKey, nonce, blockGasConsumptionTarget);

    public static Transaction[] GetTxs(Instruction[] instructions, PrivateKey privateKey, int nonce, long blockGasConsumptionTarget)
    {
        Transaction[] txs = new Transaction[2];

        // deploying contract repeatedly calling contract with push0 instructions
        txs[0] = Build.A.Transaction
            .WithNonce((UInt256)nonce)
            .WithType(TxType.EIP1559)
            .WithMaxFeePerGas(1.GWei())
            .WithMaxPriorityFeePerGas(1.GWei())
            .WithTo(null)
            .WithChainId(BlockchainIds.Holesky)
            .WithData(PrepareContractCallCode(privateKey.Address, (UInt256)nonce + 1))
            .WithGasLimit(blockGasConsumptionTarget)
            .SignedAndResolved(privateKey)
            .TestObject;

        // deploing contract with push0 instructions
        txs[1] = Build.A.Transaction
            .WithNonce((UInt256)(nonce + 1))
            .WithType(TxType.EIP1559)
            .WithMaxFeePerGas(1.GWei())
            .WithMaxPriorityFeePerGas(1.GWei())
            .WithTo(null)
            .WithChainId(BlockchainIds.Holesky)
            .WithData(PreparePush0Code(instructions))
            .WithGasLimit(blockGasConsumptionTarget)
            .SignedAndResolved(privateKey)
            .TestObject;

        return txs;
    }

    private static byte[] PrepareContractCallCode(Address senderAddress, UInt256 nonce)
    {
        Address contractAddress = ContractAddress.From(ContractAddress.From(senderAddress, nonce), 1);

        List<byte> codeToDeploy = new();

        codeToDeploy.Add((byte)Instruction.JUMPDEST);

        for (int i = 0; i < 500; i++)
        {
            codeToDeploy.Add((byte)Instruction.PUSH0);
            codeToDeploy.Add((byte)Instruction.PUSH0);
            codeToDeploy.Add((byte)Instruction.PUSH0);
            codeToDeploy.Add((byte)Instruction.PUSH0);
            codeToDeploy.Add((byte)Instruction.PUSH20);
            codeToDeploy.AddRange(contractAddress.Bytes);
            codeToDeploy.Add((byte)Instruction.PUSH2);
            codeToDeploy.Add(0xff);
            codeToDeploy.Add(0xff);
            codeToDeploy.Add((byte)Instruction.STATICCALL);
            codeToDeploy.Add((byte)Instruction.POP);
        }

        codeToDeploy.Add((byte)Instruction.PUSH0);
        codeToDeploy.Add((byte)Instruction.JUMP);

        List<byte> byteCode = ContractFactory.GenerateCodeToDeployContract(codeToDeploy);
        return byteCode.ToArray();
    }

    private static byte[] PreparePush0Code(Instruction[] instructions)
    {
        List<byte> codeToDeploy = new();

        for (int i = 0; i < 1023; i++)
        {
            foreach (Instruction instruction in instructions)
            {
                codeToDeploy.Add((byte)instruction);
            }
        }

        List<byte> byteCode = ContractFactory.GenerateCodeToDeployContract(codeToDeploy);
        return byteCode.ToArray();
    }
}
