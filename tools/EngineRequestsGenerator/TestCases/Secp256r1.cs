// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;

namespace EngineRequestsGenerator.TestCases;

public static class Secp256r1
{
    public static Transaction[] GetTxsWithValidSig(PrivateKey privateKey, int nonce, long blockGasConsumptionTarget)
    {
        return
        [
            Build.A.Transaction
                .WithNonce((UInt256) nonce)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(1.GWei())
                .WithMaxPriorityFeePerGas(1.GWei())
                .WithTo(null)
                .WithChainId(BlockchainIds.Holesky)
                .WithData(PrepareCode(privateKey, blockGasConsumptionTarget,
                    "4cee90eb86eaa050036147a12d49004b6b9c72bd725d39d4785011fe190f0b4da73bd4903f0ce3b639bbbf6e8e80d16931ff4bcf5993d58468e8fb19086e8cac36dbcd03009df8c59286b162af3bd7fcc0450c9aa81be5d10d312af6c66b1d604aebd3099c618202fcfe16ae7770b0c49ab5eadf74b754204a3bb6060e44eff37618b065f9832de4ca6ca971a7a1adc826d0f7c00181a5fb2ddf79ae00b4e10e"))
                .WithGasLimit(blockGasConsumptionTarget)
                .SignedAndResolved(privateKey)
                .TestObject
        ];
    }

    public static Transaction[] GetTxsWithInvalidSig(PrivateKey privateKey, int nonce, long blockGasConsumptionTarget)
    {
        return
        [
            Build.A.Transaction
                .WithNonce((UInt256) nonce)
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(1.GWei())
                .WithMaxPriorityFeePerGas(1.GWei())
                .WithTo(null)
                .WithChainId(BlockchainIds.Holesky)
                .WithData(PrepareCode(privateKey, blockGasConsumptionTarget,
                    "4cee90eb86eaa050036147a12d49004b6b9c72bd725d39d4785011fe190f0b4db73bd4903f0ce3b639bbbf6e8e80d16931ff4bcf5993d58468e8fb19086e8cac36dbcd03009df8c59286b162af3bd7fcc0450c9aa81be5d10d312af6c66b1d604aebd3099c618202fcfe16ae7770b0c49ab5eadf74b754204a3bb6060e44eff37618b065f9832de4ca6ca971a7a1adc826d0f7c00181a5fb2ddf79ae00b4e10e"))
                .WithGasLimit(blockGasConsumptionTarget)
                .SignedAndResolved(privateKey)
                .TestObject
        ];
    }

    private static byte[] PrepareCode(PrivateKey privateKey, long blockGasConsumptionTarget, string inputHex)
    {
        var input = Convert.FromHexString(inputHex);
        ReadOnlySpan<byte> hash = input[..32], sig = input[32..96];
        ReadOnlySpan<byte> x = input[96..128], y = input[128..160];

        List<byte> codeToDeploy = new();

        codeToDeploy.Add((byte)Instruction.PUSH32);
        codeToDeploy.AddRange(hash);
        codeToDeploy.Add((byte)Instruction.PUSH0);
        codeToDeploy.Add((byte)Instruction.MSTORE);

        codeToDeploy.Add((byte)Instruction.PUSH32);
        codeToDeploy.AddRange(sig[..32]);
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add(0x20);
        codeToDeploy.Add((byte)Instruction.MSTORE);

        codeToDeploy.Add((byte)Instruction.PUSH32);
        codeToDeploy.AddRange(sig[32..]);
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add(0x40);
        codeToDeploy.Add((byte)Instruction.MSTORE);

        codeToDeploy.Add((byte)Instruction.PUSH32);
        codeToDeploy.AddRange(x);
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add(0x60);
        codeToDeploy.Add((byte)Instruction.MSTORE);

        codeToDeploy.Add((byte)Instruction.PUSH32);
        codeToDeploy.AddRange(y);
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add(0x80);
        codeToDeploy.Add((byte)Instruction.MSTORE);

        int jumpDestPosition = codeToDeploy.Count;
        codeToDeploy.Add((byte)Instruction.JUMPDEST);

        byte[] gasTarget = blockGasConsumptionTarget.ToBigEndianByteArrayWithoutLeadingZeros();

        for (int i = 0; i < 1000; i++)
        {
            codeToDeploy.Add((byte)Instruction.PUSH1);  // return size
            codeToDeploy.Add(0x20);
            codeToDeploy.Add((byte)Instruction.PUSH1);  // return offset
            codeToDeploy.Add(0xa0);
            codeToDeploy.Add((byte)Instruction.PUSH1);  // args size
            codeToDeploy.Add(0xa0);
            codeToDeploy.Add((byte)Instruction.PUSH0);  // args offset
            codeToDeploy.Add((byte)Instruction.PUSH2);  // address
            codeToDeploy.Add(0x01);
            codeToDeploy.Add(0x00);
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
