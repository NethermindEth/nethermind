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

public static class EcMul
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
            case TestCase.EcMulInfinities2Scalar:
                return PrepareCode(UInt256.Zero.ToBigEndian(), UInt256.Zero.ToBigEndian(), ((UInt256)2).ToBigEndian());
            case TestCase.EcMulInfinities32ByteScalar:
                byte[] scalar = "25f8c89ea3437f44f8fc8b6bfbb6312074dc6f983809a5e809ff4e1d076dd585".ToBytes();
                return PrepareCode(UInt256.Zero.ToBigEndian(), UInt256.Zero.ToBigEndian(), scalar);
            case TestCase.EcMul122:
                return PrepareCode(UInt256.One.ToBigEndian(), ((UInt256)2).ToBigEndian(), ((UInt256)2).ToBigEndian());
            case TestCase.EcMul12And32ByteScalar:
                scalar = "25f8c89ea3437f44f8fc8b6bfbb6312074dc6f983809a5e809ff4e1d076dd585".ToBytes();
                return PrepareCode(UInt256.One.ToBigEndian(), ((UInt256)2).ToBigEndian(), scalar);
            case TestCase.EcMul32ByteCoordinates2Scalar:
                byte[] x = "089142debb13c461f61523586a60732d8b69c5b38a3380a74da7b2961d867dbf".ToBytes();
                byte[] y = "2d5fc7bbc013c16d7945f190b232eacc25da675c0eb093fe6b9f1b4b4e107b36".ToBytes();
                return PrepareCode(x, y, ((UInt256)2).ToBigEndian());
            case TestCase.EcMul32ByteCoordinates32ByteScalar:
                x = "089142debb13c461f61523586a60732d8b69c5b38a3380a74da7b2961d867dbf".ToBytes();
                y = "2d5fc7bbc013c16d7945f190b232eacc25da675c0eb093fe6b9f1b4b4e107b36".ToBytes();
                scalar = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff".ToBytes();
                return PrepareCode(x, y, scalar);
            default:
                throw new ArgumentOutOfRangeException(nameof(testCase), testCase, null);
        }
    }

    private static byte[] PrepareCode(byte[] x, byte[] y, byte[] scalar)
    {
        List<byte> codeToDeploy = new();

        codeToDeploy.Add((byte)Instruction.PUSH32);     // x
        codeToDeploy.AddRange(x);
        codeToDeploy.Add((byte)Instruction.PUSH0);
        codeToDeploy.Add((byte)Instruction.MSTORE);
        codeToDeploy.Add((byte)Instruction.PUSH32);     // y
        codeToDeploy.AddRange(y);
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add(0x20);
        codeToDeploy.Add((byte)Instruction.MSTORE);
        codeToDeploy.Add((byte)Instruction.PUSH32);     // scalar
        codeToDeploy.AddRange(scalar);
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add(0x40);
        codeToDeploy.Add((byte)Instruction.MSTORE);

        int jumpDestPosition = codeToDeploy.Count;
        codeToDeploy.Add((byte)Instruction.JUMPDEST);

        long gasLimit = 6000;
        byte[] gasLimitBytes = gasLimit.ToBigEndianByteArrayWithoutLeadingZeros();

        for (int i = 0; i < 1000; i++)
        {
            codeToDeploy.Add((byte)Instruction.PUSH1);  // return size
            codeToDeploy.Add(0x40);
            codeToDeploy.Add((byte)Instruction.PUSH0);  // return offset
            codeToDeploy.Add((byte)Instruction.PUSH1);  // args size
            codeToDeploy.Add(0x60);
            codeToDeploy.Add((byte)Instruction.PUSH0);  // args offset
            codeToDeploy.Add((byte)Instruction.PUSH1);  // address
            codeToDeploy.Add(0x07);
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)gasLimitBytes.Length - 1));
            codeToDeploy.AddRange(gasLimitBytes);
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
