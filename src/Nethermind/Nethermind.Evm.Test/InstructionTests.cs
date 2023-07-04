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
        public void Return_mcopy_name_for_mcopy_opcode_post_eip_5656()
        {
            Instruction.MCOPY.GetName(true, Cancun.Instance).Should().Be("MCOPY");
        }

        [Test]
        public void Return_jumpsub_name_for_mcopy_opcode_pre_eip_5656()
        {
            Instruction.MCOPY.GetName(true, Shanghai.Instance).Should().Be("JUMPSUB");
        }
    }
}
