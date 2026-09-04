// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[Parallelizable(ParallelScope.Self)]
public class ByteTests : VirtualMachineTestsBase
{
    private const string Value = "0x800102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";

    [TestCase("0x00", "0x80")]
    [TestCase("0x01", "0x01")]
    [TestCase("0x0f", "0x0f")]
    [TestCase("0x1f", "0x1f")]
    [TestCase("0x20", "0x00")]
    [TestCase("0x0100", "0x00")]
    [TestCase("0x0100000000000000000000000000000000000000000000000000000000000000", "0x00")]
    public void Extracts_big_endian_byte_or_zero_when_out_of_range(string position, string expected)
    {
        byte[] code = Prepare.EvmCode
            .PushData(Value)
            .PushData(position)
            .Op(Instruction.BYTE)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;

        _ = Execute(code);
        AssertStorage(0, Bytes.FromHexString(expected));
    }
}
