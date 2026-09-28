// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// MUL, DIV, SDIV, MOD and SMOD read both operands in place from their stack slots; DUP1 puts equal operands in
/// adjacent slots, the case where an in-place read would first show an aliasing mistake.
/// </summary>
public class InPlaceMathOperandTests : VirtualMachineTestsBase
{
    [Test]
    public void Equal_operands_in_adjacent_slots(
        [Values(Instruction.MUL, Instruction.DIV, Instruction.SDIV, Instruction.MOD, Instruction.SMOD)] Instruction op,
        [Values("0x01",
            "0x0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "0xfedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210")] string operand)
    {
        byte[] value = Bytes.FromHexString(operand);
        byte[] code = Prepare.EvmCode
            .PushData(value)
            .Op(Instruction.DUP1)
            .Op(op)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;

        _ = Execute(code);

        BigInteger x = new(value, isUnsigned: true, isBigEndian: true);
        BigInteger expected = op switch
        {
            Instruction.MUL => x * x % BigInteger.Pow(2, 256),
            Instruction.DIV or Instruction.SDIV => BigInteger.One,
            _ => BigInteger.Zero,
        };
        AssertStorage(UInt256.Zero, expected);
    }
}
