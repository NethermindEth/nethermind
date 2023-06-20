// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class InstructionTests
    {
        [Test]
        public void Return_difficulty_name_for_prevrandao_opcode_for_pre_merge()
        {
            Instruction.PREVRANDAO.GetName(false, Cancun.Instance).Should().Be("DIFFICULTY");
        }

        [Test]
        public void Return_prevrandao_name_for_prevrandao_opcode_for_post_merge()
        {
            Instruction.PREVRANDAO.GetName(true, Cancun.Instance).Should().Be("PREVRANDAO");
        }

        [Test]
        public void Return_tload_name_for_beginsub_opcode_for_eip1153()
        {
            Instruction.BEGINSUB.GetName(true, Cancun.Instance).Should().Be("TLOAD");
        }

        [Test]
        public void Return_beginsub_name_for_beginsub_opcode_for_eip1153()
        {
            Instruction.BEGINSUB.GetName(true, Shanghai.Instance).Should().Be("BEGINSUB");
        }

        [Test]
        public void Return_returnsub_name_for_returnsub_opcode_for_eip1153()
        {
            Instruction.RETURNSUB.GetName(true, Shanghai.Instance).Should().Be("RETURNSUB");
        }

        [Test]
        public void Return_tstore_name_for_returnsub_opcode_for_eip1153()
        {
            Instruction.RETURNSUB.GetName(true, Cancun.Instance).Should().Be("TSTORE");
        }
    }
}
