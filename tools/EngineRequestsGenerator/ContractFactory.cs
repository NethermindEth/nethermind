// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Evm;

namespace EngineRequestsGenerator;

public static class ContractFactory
{
    public static List<byte> GenerateCodeToDeployContract(List<byte> codeToDeploy)
    {
        List<byte> initCode = GenerateInitCode(codeToDeploy);
        List<byte> byteCode = new();

        for (long i = 0; i < initCode.Count; i += 32)
        {
            List<byte> currentWord = i == 0
                ? initCode.Slice(0, initCode.Count % 32)
                : initCode.Slice((int)i - 32 + initCode.Count % 32, 32);
            byteCode.Add((byte)(Instruction.PUSH1 + (byte)currentWord.Count - 1));
            byteCode.AddRange(currentWord);

            // push memory offset - i
            byte[] memoryOffset = i.ToBigEndianByteArrayWithoutLeadingZeros();
            if (memoryOffset is [0])
            {
                byteCode.Add((byte)Instruction.PUSH0);
            }
            else
            {
                byteCode.Add((byte)(Instruction.PUSH1 + (byte)memoryOffset.Length - 1));
                byteCode.AddRange(memoryOffset);
            }

            // save in memory
            byteCode.Add((byte)Instruction.MSTORE);
        }

        // push size of init code to read from memory
        byte[] sizeOfInitCode = initCode.Count.ToByteArray().WithoutLeadingZeros().ToArray();
        byteCode.Add((byte)(Instruction.PUSH1 + (byte)sizeOfInitCode.Length - 1));
        byteCode.AddRange(sizeOfInitCode);

        // offset in memory
        byteCode.Add((byte)(Instruction.PUSH1));
        byteCode.AddRange(new[] { (byte)(32 - (initCode.Count % 32)) });

        // 0 wei to send
        byteCode.Add((byte)Instruction.PUSH0);

        byteCode.Add((byte)Instruction.CREATE);

        Console.WriteLine($"size of prepared code: {byteCode.Count}");

        return byteCode;
    }

    private static List<byte> GenerateInitCode(List<byte> codeToDeploy)
    {
        List<byte> initCode = new();

        for (long i = 0; i < codeToDeploy.Count; i += 32)
        {
            List<byte> currentWord = i == 0
                ? codeToDeploy.Slice(0, codeToDeploy.Count % 32)
                : codeToDeploy.Slice((int)i - 32 + codeToDeploy.Count % 32, 32);

            initCode.Add((byte)(Instruction.PUSH1 + (byte)currentWord.Count - 1));
            initCode.AddRange(currentWord);

            // push memory offset - i
            byte[] memoryOffset = i.ToBigEndianByteArrayWithoutLeadingZeros();
            if (memoryOffset is [0])
            {
                initCode.Add((byte)Instruction.PUSH0);
            }
            else
            {
                initCode.Add((byte)(Instruction.PUSH1 + (byte)memoryOffset.Length - 1));
                initCode.AddRange(memoryOffset);
            }

            // save in memory
            initCode.Add((byte)Instruction.MSTORE);
        }

        // push size of memory read
        byte[] sizeOfCodeToDeploy = codeToDeploy.Count.ToByteArray().WithoutLeadingZeros().ToArray();
        initCode.Add((byte)(Instruction.PUSH1 + (byte)sizeOfCodeToDeploy.Length - 1));
        initCode.AddRange(sizeOfCodeToDeploy);

        // push memory offset
        initCode.Add((byte)(Instruction.PUSH1));
        initCode.AddRange(new[] { (byte)(32 - (codeToDeploy.Count % 32)) });

        // add return opcode
        initCode.Add((byte)(Instruction.RETURN));

        return initCode;
    }
}
