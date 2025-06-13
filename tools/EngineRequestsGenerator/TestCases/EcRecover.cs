// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;

namespace EngineRequestsGenerator.TestCases;

public static class EcRecover
{
    public static Transaction[] GetTxs(PrivateKey privateKey, int nonce, long blockGasConsumptionTarget)
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
            .WithData(PrepareCode(privateKey, blockGasConsumptionTarget))
            .WithGasLimit(blockGasConsumptionTarget)
            .SignedAndResolved(privateKey)
            .TestObject
        ];
    }

    private static byte[] PrepareCode(PrivateKey privateKey, long blockGasConsumptionTarget)
    {
        List<byte> codeToDeploy = new();

        byte[] gasTarget = blockGasConsumptionTarget.ToBigEndianByteArrayWithoutLeadingZeros();

        Transaction tx = Build.A.Transaction
            .WithNonce(UInt256.Zero)
            .WithType(TxType.EIP1559)
            .WithMaxFeePerGas(1.GWei())
            .WithMaxPriorityFeePerGas(1.GWei())
            .WithTo(TestItem.AddressA)
            .WithChainId(BlockchainIds.Holesky)
            .WithGasLimit(blockGasConsumptionTarget)
            .SignedAndResolved(privateKey)
            .TestObject;

        // codeToDeploy.Add((byte)Instruction.PUSH32);
        // codeToDeploy.AddRange(tx.Hash.Bytes);
        // codeToDeploy.Add((byte)Instruction.PUSH0);
        // codeToDeploy.Add((byte)Instruction.MSTORE);

        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)tx.Signature!.V);
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add(0x20);
        codeToDeploy.Add((byte)Instruction.MSTORE);

        codeToDeploy.Add((byte)Instruction.PUSH32);
        codeToDeploy.AddRange(tx.Signature.R.ToArray());
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add(0x40);
        codeToDeploy.Add((byte)Instruction.MSTORE);

        codeToDeploy.Add((byte)Instruction.PUSH32);
        codeToDeploy.AddRange(tx.Signature.S.ToArray());
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add(0x60);
        codeToDeploy.Add((byte)Instruction.MSTORE);

        // push iterator
        codeToDeploy.Add((byte)Instruction.PUSH0);

        int jumpDestPosition = codeToDeploy.Count;
        codeToDeploy.Add((byte)Instruction.JUMPDEST);

        for (int i = 0; i < 1000; i++)
        {
            codeToDeploy.Add((byte)Instruction.DUP1);
            codeToDeploy.Add((byte)Instruction.PUSH0);
            codeToDeploy.Add((byte)Instruction.MSTORE);

            codeToDeploy.Add((byte)Instruction.PUSH1);  // return size
            codeToDeploy.Add(0x20);
            codeToDeploy.Add((byte)Instruction.PUSH1);  // return offset
            codeToDeploy.Add(0x80);
            codeToDeploy.Add((byte)Instruction.PUSH1);  // args size
            codeToDeploy.Add(0x80);
            codeToDeploy.Add((byte)Instruction.PUSH0);  // args offset
            codeToDeploy.Add((byte)Instruction.PUSH1);  // address
            codeToDeploy.Add(0x01);
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)gasTarget.Length - 1));
            codeToDeploy.AddRange(gasTarget);
            codeToDeploy.Add((byte)Instruction.STATICCALL);
            codeToDeploy.Add((byte)Instruction.POP);

            // Stack: [..., i]
            codeToDeploy.Add((byte)Instruction.PUSH1);
            codeToDeploy.Add(0x01);
            codeToDeploy.Add((byte)Instruction.ADD); // i = i + 1
            // Stack: [..., i_new]
        }

        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)jumpDestPosition);
        codeToDeploy.Add((byte)Instruction.JUMP);

        List<byte> byteCode = ContractFactory.GenerateCodeToDeployContract(codeToDeploy);
        string code = byteCode.ToArray().ToHexString();
        return byteCode.ToArray();
    }
}
