// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[Parallelizable(ParallelScope.Self)]
public class AddTests : VirtualMachineTestsBase
{
    [TestCase(
        "0x000000000000000000000000000000000000000000000000ffffffffffffffff",
        "0x0000000000000000000000000000000000000000000000000000000000000001",
        "0x0000000000000000000000000000000000000000000000010000000000000000")]
    [TestCase(
        "0x00000000000000000000000000000000ffffffffffffffffffffffffffffffff",
        "0x0000000000000000000000000000000000000000000000000000000000000001",
        "0x0000000000000000000000000000000100000000000000000000000000000000")]
    [TestCase(
        "0x0000000000000000ffffffffffffffffffffffffffffffffffffffffffffffff",
        "0x0000000000000000000000000000000000000000000000000000000000000001",
        "0x0000000000000001000000000000000000000000000000000000000000000000")]
    [TestCase(
        "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
        "0x0000000000000000000000000000000000000000000000000000000000000001",
        "0x0000000000000000000000000000000000000000000000000000000000000000")]
    public void Add_carries_across_words(string aHex, string bHex, string resultHex)
    {
        byte[] code = Prepare.EvmCode
            .PushData(Bytes.FromHexString(aHex))
            .PushData(Bytes.FromHexString(bHex))
            .Op(Instruction.ADD)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;

        _ = Execute(code);
        AssertStorage(UInt256.Zero, Bytes.FromHexString(resultHex));
    }
}
