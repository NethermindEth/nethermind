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
            case TestCase.Modexp208GasBalanced:
                return PrepareCode(32, 40);
            case TestCase.Modexp215GasExpHeavy:
                return PrepareCode(8, 648);
            case TestCase.Modexp298GasExpHeavy:
                return PrepareCode(8, 896);
            case TestCase.ModexpPawel2:
                return PrepareCode(16, 320);
            case TestCase.ModexpPawel3:
                return PrepareCode(24, 168);
            case TestCase.ModexpPawel4:
                return PrepareCode(32, 96);
            case TestCase.Modexp408GasBaseHeavy:
                return PrepareCode(280, 2);
            case TestCase.Modexp400GasExpHeavy:
                return PrepareCode(16, 301);
            case TestCase.Modexp408GasBalanced:
                return PrepareCode(48, 35);
            case TestCase.Modexp616GasBaseHeavy:
                return PrepareCode(344, 2);
            case TestCase.Modexp600GasExpHeavy:
                return PrepareCode(16, 451);
            case TestCase.Modexp600GasBalanced:
                return PrepareCode(48, 51);
            case TestCase.Modexp800GasBaseHeavy:
                return PrepareCode(392, 2);
            case TestCase.Modexp800GasExpHeavy:
                return PrepareCode(16, 601);
            case TestCase.Modexp767GasBalanced:
                return PrepareCode(56, 48);
            case TestCase.Modexp852GasExpHeavy:
                return PrepareCode(16, 640);
            case TestCase.Modexp867GasBaseHeavy:
                return PrepareCode(408, 2);
            case TestCase.Modexp996GasBalanced:
                return PrepareCode(56, 63);
            case TestCase.Modexp1045GasBaseHeavy:
                return PrepareCode(448, 2);
            case TestCase.Modexp677GasBaseHeavy:
                return PrepareCode(32, 128);
            case TestCase.Modexp765GasExpHeavy:
                return PrepareCode(24, 256);
            case TestCase.Modexp1360GasBalanced:
                return PrepareCode(256, 2);
            case TestCase.ModexpMod8Exp648:
                return PrepareCode(8, 648);
            case TestCase.ModexpMod8Exp896:
                return PrepareCode(8, 896);
            case TestCase.ModexpMod32Exp32:
                return PrepareCode(32, 32);
            case TestCase.ModexpMod32Exp36:
                return PrepareCode(32, 36);
            case TestCase.ModexpMod32Exp40:
                return PrepareCode(32, 40);
            case TestCase.ModexpMod32Exp64:
                return PrepareCode(32, 64);
            case TestCase.ModexpMod32Exp65:
                return PrepareCode(32, 65);
            case TestCase.ModexpMod32Exp128:
                return PrepareCode(32, 128);
            case TestCase.ModexpMod256Exp2:
                return PrepareCode(256, 2);
            case TestCase.ModexpMod264Exp2:
                return PrepareCode(264, 2);
            case TestCase.ModexpMod1024Exp2:
                return PrepareCode(1024, 2);
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
        long initialBaseOffset = offset;
        for (int i = 0; i < baseSize / 32; i++)                                             // full words
        {
            byte[] offsetInternal = offset.ToBigEndianByteArrayWithoutLeadingZeros();
            codeToDeploy.Add((byte)Instruction.PUSH32);                                 // base
            for (int j = 0; j < 32; j++)                                       // preparing worst case (0xFF..FF)
            {
                codeToDeploy.Add(0xFF);
            }
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)offsetInternal.Length - 1));
            codeToDeploy.AddRange(offsetInternal);
            codeToDeploy.Add((byte)Instruction.MSTORE);
            offset += 32;
        }
        for (int i = 0; i < baseSize % 32; i++)                                             // single bytes
        {
            byte[] offsetInternal = offset.ToBigEndianByteArrayWithoutLeadingZeros();
            codeToDeploy.Add((byte)Instruction.PUSH1);                                  // base
            codeToDeploy.Add(0xFF);
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
            codeToDeploy.Add((byte)Instruction.PUSH32);                         // modulo
            for (int j = 0; j < 31; j++)                                       // preparing worst case (0xFF..FF00)
            {
                codeToDeploy.Add(0xFF);
            }
            codeToDeploy.Add(0x00);
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)offsetInternal.Length - 1));
            codeToDeploy.AddRange(offsetInternal);
            codeToDeploy.Add((byte)Instruction.MSTORE);
            offset += 32;
        }
        for (int i = 0; i + 1 < baseSize % 32; i++)                                             // single bytes
        {
            byte[] offsetInternal = offset.ToBigEndianByteArrayWithoutLeadingZeros();
            codeToDeploy.Add((byte)Instruction.PUSH1);                                  // modulo
            codeToDeploy.Add(0xFF);
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)offsetInternal.Length - 1));
            codeToDeploy.AddRange(offsetInternal);
            codeToDeploy.Add((byte)Instruction.MSTORE8);
            offset += 1;
        }

        // push iterator
        codeToDeploy.Add((byte)Instruction.PUSH0);

        long jumpDestPosition = codeToDeploy.Count;
        byte[] jumpDestBytes = jumpDestPosition.ToBigEndianByteArrayWithoutLeadingZeros();
        codeToDeploy.Add((byte)Instruction.JUMPDEST);
        Console.WriteLine($"jumpdest: {jumpDestPosition}");

        byte[] argsSize = ((long)(32 + 32 + 32 + baseSize + exponentAsBytes.Length + baseSize)).ToBigEndianByteArrayWithoutLeadingZeros();

        for (int i = 0; i < 1000; i++)
        {
            // override base (one byte)
            byte[] offsetInternal = (initialBaseOffset + i / 256).ToBigEndianByteArrayWithoutLeadingZeros();
            codeToDeploy.Add((byte)Instruction.PUSH1);                                  // base
            codeToDeploy.Add((byte)(i % 256));
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)offsetInternal.Length - 1));
            codeToDeploy.AddRange(offsetInternal);
            codeToDeploy.Add((byte)Instruction.MSTORE8);


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

        // Stack: [..., i]
        codeToDeploy.Add((byte)Instruction.PUSH1);
        codeToDeploy.Add(0x01);
        codeToDeploy.Add((byte)Instruction.ADD); // i = i + 1
        // Stack: [..., i_new]

        // reset base
        for (int i = 0; i < 4; i++)                                             // single bytes
        {
            byte[] offsetInternal = (initialBaseOffset + i).ToBigEndianByteArrayWithoutLeadingZeros();
            codeToDeploy.Add((byte)Instruction.PUSH1);                                  // base
            codeToDeploy.Add(0xFF);
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)offsetInternal.Length - 1));
            codeToDeploy.AddRange(offsetInternal);
            codeToDeploy.Add((byte)Instruction.MSTORE8);
        }

        // override base bytes 4-6 (to have iterator)
        for (int i = 0; i < 3; i++)
        {
            byte[] offsetInternal = (initialBaseOffset + i + 4).ToBigEndianByteArrayWithoutLeadingZeros();
            codeToDeploy.Add((byte)Instruction.DUP1);                           // Stack: [..., i, i] it is our iterator
            codeToDeploy.Add((byte)Instruction.PUSH1);
            codeToDeploy.Add((byte)(i * 8));                                    // one byte has 8 bits
            codeToDeploy.Add((byte)Instruction.SHR);                            // shifting i last bytes
            codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)offsetInternal.Length - 1));
            codeToDeploy.AddRange(offsetInternal);
            codeToDeploy.Add((byte)Instruction.MSTORE8);
        }


        codeToDeploy.Add((byte)(Instruction.PUSH1 + (byte)jumpDestBytes.Length - 1));
        codeToDeploy.AddRange(jumpDestBytes);
        codeToDeploy.Add((byte)Instruction.JUMP);

        List<byte> byteCode = ContractFactory.GenerateCodeToDeployContract(codeToDeploy);
        string code = byteCode.ToArray().ToHexString();
        return byteCode.ToArray();
    }
}
