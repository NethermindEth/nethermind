// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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
            Instruction.PREVRANDAO.GetName(false).Should().Be("DIFFICULTY");
        }

        [Test]
        public void Return_prevrandao_name_for_prevrandao_opcode_for_post_merge()
        {
            Instruction.PREVRANDAO.GetName(true).Should().Be("PREVRANDAO");
        }

        [Test]
        public void Return_rjump_name_for_beginsub_opcode_for_post_eof()
        {
            Instruction.RJUMP.GetName(true, Cancun.Instance).Should().Be("RJUMP");
        }


        [Test]
        public void Return_beginsub_name_for_beginsub_opcode_for_pre_eof()
        {
            Instruction.BEGINSUB.GetName(true, GrayGlacier.Instance).Should().Be("BEGINSUB");
        }

        [Test]
        public void Return_rjumpi_name_for_returnsub_opcode_for_post_eof()
        {
            Instruction.RJUMPI.GetName(true, Cancun.Instance).Should().Be("RJUMPI");
        }


        [Test]
        public void Return_returnsub_name_for_returnsub_opcode_for_pre_eof()
        {
            Instruction.RETURNSUB.GetName(true, GrayGlacier.Instance).Should().Be("RETURNSUB");
        }


        [Test]
        public void Return_rjumpv_name_for_jumpsub_opcode_for_post_eof()
        {
            Instruction.RJUMPV.GetName(true, Cancun.Instance).Should().Be("RJUMPV");
        }


        [Test]
        public void Return_jumpsub_name_for_jumpsub_opcode_for_pre_eof()
        {
            Instruction.RJUMPV.GetName(true, GrayGlacier.Instance).Should().Be("JUMPSUB");
        }
    }
}
