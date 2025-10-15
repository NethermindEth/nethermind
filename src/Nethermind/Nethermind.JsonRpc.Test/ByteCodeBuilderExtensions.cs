using System;
using System.Text;
using Nethermind.Core.Extensions;
using Nethermind.Evm;

namespace Nethermind.JsonRpc.Test;

public static class ByteCodeBuilderExtensions
{
    public static Prepare RevertWithSolidityErrorEncoding(this Prepare prepare, string errorMessage)
    {
        // based on https://docs.soliditylang.org/en/latest/control-structures.html#revert
        // 0x08c379a0                                                         // Function selector for Error(string)
        // 0x0000000000000000000000000000000000000000000000000000000000000020 // Data offset
        // 0x000000000000000000000000000000000000000000000000000000000000001a // String length
        // 0x4e6f7420656e6f7567682045746865722070726f76696465642e000000000000 // String data

        byte[] errorMessageBytes = Encoding.UTF8.GetBytes(errorMessage);
        byte[] errorSelector = Bytes.FromHexString("0x08c379a0"); // Error(string) selector
        int paddedStringLength = ((errorMessageBytes.Length + 31) / 32) * 32;
        int totalLength = 4 + 32 + 32 + paddedStringLength;

        prepare
            // Build the first 32 bytes: selector (4 bytes) + start of offset (28 bytes)
            // We want: 0x08c379a0 followed by 28 zero bytes
            // Need to shift left: 0x08c379a0 << 224 bits = 0x08c379a0000...000
            .PushData(errorSelector)
            .PushData(224)  // 28 bytes * 8 bits = 224 bits
            .Op(Instruction.SHL)  // Shift left to position selector at bytes 0-3
            .PushData(0)
            .Op(Instruction.MSTORE)

            // Store offset to string data (32) at memory offset 4
            // This will occupy bytes 4-35
            .PushData(32)
            .PushData(4)
            .Op(Instruction.MSTORE)

            // Store string length at memory offset 36
            // This will occupy bytes 36-67
            .PushData(errorMessageBytes.Length)
            .PushData(36)
            .Op(Instruction.MSTORE)

            // Store actual string data at memory offset 68
            .StoreDataInMemory(68, errorMessageBytes)

            // REVERT(offset=0, length=totalLength)
            .PushData(totalLength)
            .PushData(0)
            .Op(Instruction.REVERT);

        return prepare;
    }
}
