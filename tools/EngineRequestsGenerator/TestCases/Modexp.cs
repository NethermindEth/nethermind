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

public static class Modexp
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
            case TestCase.ModexpMinGasBaseHeavy:
                return PrepareCode(192, 2);
            case TestCase.ModexpMinGasExpHeavy:
                return PrepareCode(8, 603);
            case TestCase.ModexpMinGasBalanced:
                return PrepareCode(40, 25);
            case TestCase.Modexp215GasExpHeavy:
                return PrepareCode(8, 648);
            // case TestCase.Modexp1KGasBaseHeavy:
            //     return PrepareCode(440, 2);
            // case TestCase.Modexp1KGasBalanced:
            //     return PrepareCode(56, 62);
            // case TestCase.Modexp10KGasExpHeavy:
            //     return PrepareCode(48, 25);
            // case TestCase.Modexp135KGasBalanced:
            //     return PrepareCode(200, 648);
            default:
                throw new ArgumentOutOfRangeException(nameof(testCase), testCase, null);
        }
    }

    private static byte[] PrepareCode(long baseSize, int numberOfBinaryOnes)
    {
        List<byte> codeToDeploy = new();

        long words = (baseSize + 7) / 8;
        long multiplicationComplexity = words * words;

        byte[] byteSizeOfBase = baseSize.ToBigEndianByteArrayWithoutLeadingZeros();

        int nonFullByte = numberOfBinaryOnes % 8;
        int numberOfBytesInExponent = numberOfBinaryOnes / 8;
        numberOfBytesInExponent += nonFullByte == 0 ? 0 : 1;
        byte[] exponentAsBytes = new byte[numberOfBytesInExponent];

        if (nonFullByte == 0)
        {
            exponentAsBytes[0] = 0xFF;
        }
        else
        {
            int nonFullByteValue = 1;
            for (byte i = 1; i < nonFullByte; i++)
            {
                nonFullByteValue += i * 2;
            }

            exponentAsBytes[0] = (byte)nonFullByteValue;
        }

        for (int i = 1; i < exponentAsBytes.Length; i++)
        {
            exponentAsBytes[i] = 0xFF;
        }


        byte[] byteSizeOfExponent = ((long)exponentAsBytes.Length).ToBigEndianByteArrayWithoutLeadingZeros();

        long iterationCount = numberOfBinaryOnes - 1;
        // long gasConsumptionTarget = Math.Max(200, multiplicationComplexity * iterationCount / 3);
        long gasConsumptionTarget = 65535;


        byte[] gasTarget = gasConsumptionTarget.ToBigEndianByteArrayWithoutLeadingZeros();

        long offset = 0;

        codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)byteSizeOfBase.Length - 1));      // byte size of base
        codeToDeploy.AddRange(byteSizeOfBase);
        codeToDeploy.Add((byte)Instruction.PUSH0);
        codeToDeploy.Add((byte)Instruction.MSTORE);
        offset += 32;

        codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)byteSizeOfExponent.Length - 1));  // byte size of exponent
        codeToDeploy.AddRange(byteSizeOfExponent);
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)32);
        codeToDeploy.Add((byte)Instruction.MSTORE);
        offset += 32;

        codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)byteSizeOfBase.Length - 1));      // byte size of modulo
        codeToDeploy.AddRange(byteSizeOfBase);
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add((byte)64);
        codeToDeploy.Add((byte)Instruction.MSTORE);
        offset += 32;

        // base
        for (int i = 0; i < baseSize / 32; i++)                                             // full words
        {
            byte[] offsetInternal = offset.ToBigEndianByteArrayWithoutLeadingZeros();
            codeToDeploy.Add((byte)Instruction.PUSH32);                                 // base
            codeToDeploy.AddRange(Keccak.Compute(i.ToByteArray()).Bytes);            // kind of random value
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)offsetInternal.Length - 1));
            codeToDeploy.AddRange(offsetInternal);
            codeToDeploy.Add((byte)Instruction.MSTORE);
            offset += 32;
        }
        for (int i = 0; i < baseSize % 32; i++)                                             // single bytes
        {
            byte[] offsetInternal = offset.ToBigEndianByteArrayWithoutLeadingZeros();
            codeToDeploy.Add((byte)Instruction.PUSH1);                                  // base
            codeToDeploy.Add((byte)i);
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)offsetInternal.Length - 1));
            codeToDeploy.AddRange(offsetInternal);
            codeToDeploy.Add((byte)Instruction.MSTORE8);
            offset += 1;
        }

        // exponent
        for (int i = 0; i < exponentAsBytes.Length / 32; i++)                               // full words
        {
            byte[] offsetInternal = offset.ToBigEndianByteArrayWithoutLeadingZeros();
            codeToDeploy.Add((byte)Instruction.PUSH32);
            codeToDeploy.AddRange(exponentAsBytes.Slice(i * 32, 32));       // exponent - 32 bytes
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)offsetInternal.Length - 1));
            codeToDeploy.AddRange(offsetInternal);
            codeToDeploy.Add((byte)Instruction.MSTORE);
            offset += 32;
        }
        for (int i = 0; i < exponentAsBytes.Length % 32; i++)                              // single bytes
        {
            byte[] offsetInternal = offset.ToBigEndianByteArrayWithoutLeadingZeros();
            codeToDeploy.Add((byte)Instruction.PUSH1);
            codeToDeploy.Add((byte)exponentAsBytes[(exponentAsBytes.Length / 32) * 32 + i]);   // exponent - 1 byte
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)offsetInternal.Length - 1));
            codeToDeploy.AddRange(offsetInternal);
            codeToDeploy.Add((byte)Instruction.MSTORE8);
            offset += 1;
        }

        // modulo
        for (int i = 0; i < baseSize / 32; i++)
        {
            byte[] offsetInternal = offset.ToBigEndianByteArrayWithoutLeadingZeros();
            codeToDeploy.Add((byte)Instruction.PUSH32);                                 // modulo
            codeToDeploy.AddRange(Keccak.Compute((i + 100).ToByteArray()).Bytes);    // kind of random value
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)offsetInternal.Length - 1));
            codeToDeploy.AddRange(offsetInternal);
            codeToDeploy.Add((byte)Instruction.MSTORE);
            offset += 32;
        }
        for (int i = 0; i < baseSize % 32; i++)                                             // single bytes
        {
            byte[] offsetInternal = offset.ToBigEndianByteArrayWithoutLeadingZeros();
            codeToDeploy.Add((byte)Instruction.PUSH1);                                  // modulo
            codeToDeploy.Add((byte)i);
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)offsetInternal.Length - 1));
            codeToDeploy.AddRange(offsetInternal);
            codeToDeploy.Add((byte)Instruction.MSTORE8);
            offset += 1;
        }

        int jumpDestPosition = codeToDeploy.Count;
        codeToDeploy.Add((byte)Instruction.JUMPDEST);

        byte[] argsSize = ((long)(32 + 32 + 32 + baseSize + exponentAsBytes.Length + baseSize)).ToBigEndianByteArrayWithoutLeadingZeros();

        for (int i = 0; i < 500; i++)
        {
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)byteSizeOfBase.Length - 1));      // return size
            codeToDeploy.AddRange(byteSizeOfBase);
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)argsSize.Length - 1));            // return offset
            codeToDeploy.AddRange(argsSize);
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)argsSize.Length - 1));            // args size
            codeToDeploy.AddRange(argsSize);
            codeToDeploy.Add((byte)Instruction.PUSH0);                                          // args offset
            codeToDeploy.Add((byte)Instruction.PUSH1);                                          // address
            codeToDeploy.Add(0x05);
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
