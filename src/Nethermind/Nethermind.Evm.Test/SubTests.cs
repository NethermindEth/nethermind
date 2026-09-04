// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[Parallelizable(ParallelScope.Self)]
public class SubTests : VirtualMachineTestsBase
{
    [TestCase(
        "0x0000000000000000000000000000000000000000000000000000000000000005",
        "0x0000000000000000000000000000000000000000000000000000000000000003",
        "0x0000000000000000000000000000000000000000000000000000000000000002")]
    [TestCase(
        "0x0000000000000000000000000000000000000000000000010000000000000000",
        "0x0000000000000000000000000000000000000000000000000000000000000001",
        "0x000000000000000000000000000000000000000000000000ffffffffffffffff")]
    [TestCase(
        "0x0000000000000000000000000000000100000000000000000000000000000000",
        "0x0000000000000000000000000000000000000000000000000000000000000001",
        "0x00000000000000000000000000000000ffffffffffffffffffffffffffffffff")]
    [TestCase(
        "0x0000000000000001000000000000000000000000000000000000000000000000",
        "0x0000000000000000000000000000000000000000000000000000000000000001",
        "0x0000000000000000ffffffffffffffffffffffffffffffffffffffffffffffff")]
    [TestCase(
        "0x0000000000000000000000000000000000000000000000000000000000000000",
        "0x0000000000000000000000000000000000000000000000000000000000000001",
        "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    public void Subtract_borrows_across_words(string aHex, string bHex, string resultHex)
    {
        byte[] code = Prepare.EvmCode
            .PushData(Bytes.FromHexString(bHex))
            .PushData(Bytes.FromHexString(aHex))
            .Op(Instruction.SUB)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;

        _ = Execute(code);
        AssertStorage(UInt256.Zero, Bytes.FromHexString(resultHex));
    }
}
