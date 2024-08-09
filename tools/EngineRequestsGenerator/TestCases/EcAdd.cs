// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Int256;

namespace EngineRequestsGenerator.TestCases;

public static class EcAdd
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
            case TestCase.EcAddInfinities:
                return PrepareCode(UInt256.Zero.ToBigEndian(), UInt256.Zero.ToBigEndian(), UInt256.Zero.ToBigEndian(), UInt256.Zero.ToBigEndian());
            case TestCase.EcAdd12:
                return PrepareCode(UInt256.One.ToBigEndian(), ((UInt256)2).ToBigEndian(), UInt256.One.ToBigEndian(), ((UInt256)2).ToBigEndian());
            case TestCase.EcAdd32ByteCoordinates:
                byte[] x1 = "089142debb13c461f61523586a60732d8b69c5b38a3380a74da7b2961d867dbf".ToBytes();
                byte[] y1 = "2d5fc7bbc013c16d7945f190b232eacc25da675c0eb093fe6b9f1b4b4e107b36".ToBytes();
                byte[] x2 = "25f8c89ea3437f44f8fc8b6bfbb6312074dc6f983809a5e809ff4e1d076dd585".ToBytes();
                byte[] y2 = "0b38c7ced6e4daef9c4347f370d6d8b58f4b1d8dc61a3c59d651a0644a2a27cf".ToBytes();
                return PrepareCode(x1, y1, x2, y2);
            default:
                throw new ArgumentOutOfRangeException(nameof(testCase), testCase, null);
        }
    }

    private static byte[] PrepareCode(byte[] x1, byte[] y1, byte[] x2, byte[] y2)
    {
        List<byte> codeToDeploy = new();

        codeToDeploy.Add((byte)Instruction.PUSH32);     // x1
        codeToDeploy.AddRange(x1);
        codeToDeploy.Add((byte)Instruction.PUSH0);
        codeToDeploy.Add((byte)Instruction.MSTORE);
        codeToDeploy.Add((byte)Instruction.PUSH32);     // y1
        codeToDeploy.AddRange(y1);
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add(0x20);
        codeToDeploy.Add((byte)Instruction.MSTORE);
        codeToDeploy.Add((byte)Instruction.PUSH32);     // x2
        codeToDeploy.AddRange(x2);
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add(0x40);
        codeToDeploy.Add((byte)Instruction.MSTORE);
        codeToDeploy.Add((byte)Instruction.PUSH32);     // y2
        codeToDeploy.AddRange(y2);
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add(0x60);
        codeToDeploy.Add((byte)Instruction.MSTORE);

        int jumpDestPosition = codeToDeploy.Count;
        codeToDeploy.Add((byte)Instruction.JUMPDEST);

        for (int i = 0; i < 1000; i++)
        {
            codeToDeploy.Add((byte)Instruction.PUSH1);  // return size
            codeToDeploy.Add(0x40);
            codeToDeploy.Add((byte)Instruction.PUSH1);  // return offset
            codeToDeploy.Add(0x80);
            codeToDeploy.Add((byte)Instruction.PUSH1);  // args size
            codeToDeploy.Add(0x80);
            codeToDeploy.Add((byte)Instruction.PUSH0);  // args offset
            codeToDeploy.Add((byte)Instruction.PUSH1);  // address
            codeToDeploy.Add(0x06);
            codeToDeploy.Add((byte)Instruction.PUSH1);
            codeToDeploy.Add((byte)150);
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
