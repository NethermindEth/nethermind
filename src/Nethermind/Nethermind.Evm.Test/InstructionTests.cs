// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
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
    }
}
