// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>What <see cref="OpcodeBodySet"/> reports about each fork's untraced dispatch table.</summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class OpcodeBodySetTests
{
    private static IEnumerable<IReleaseSpec> Forks()
    {
        yield return Frontier.Instance;
        yield return Homestead.Instance;
        yield return Byzantium.Instance;
        yield return Constantinople.Instance;
        yield return Istanbul.Instance;
        yield return Berlin.Instance;
        yield return London.Instance;
        yield return Paris.Instance;
        yield return Shanghai.Instance;
        yield return Cancun.Instance;
        yield return Prague.Instance;
        yield return Osaka.Instance;
    }

    private static OpcodeBodySet Of(IReleaseSpec spec) => VirtualMachine<EthereumGasPolicy>.GetOpcodeBodySet(spec);

    [TestCaseSource(nameof(Forks))]
    public void Records_every_entry(IReleaseSpec spec)
    {
        OpcodeBodySet set = Of(spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(set.Bodies, Has.Count.EqualTo(256));
            Assert.That(set.Bodies.Select(static (body, opcode) => (opcode, body.Flow)).Where(static e => e.Flow == OpcodeFlow.Unrecorded),
                Is.Empty, "every table entry maps back to the body that built it");
        }
    }

    [TestCaseSource(nameof(Forks))]
    public void Gives_every_body_an_entry_point(IReleaseSpec spec)
    {
        OpcodeBodySet set = Of(spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(set.Bodies.Where(static body => body.BodyType is not null).Select(static body => body.Execute), Is.All.Not.EqualTo((nint)0),
                "a body with a type is callable through its entry point");
            Assert.That(set.Bodies[(int)Instruction.JUMPI].Execute, Is.EqualTo((nint)0), "JUMPI is run by its handler, not a body");
        }
    }

    [TestCaseSource(nameof(Forks))]
    public void Describes_jumpi_and_add_as_dispatch_charges_them(IReleaseSpec spec)
    {
        OpcodeBodySet set = Of(spec);
        OpcodeBody jumpIf = set.Bodies[(int)Instruction.JUMPI];
        OpcodeBody add = set.Bodies[(int)Instruction.ADD];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(jumpIf.Flow, Is.EqualTo(OpcodeFlow.ConditionalJump));
            Assert.That(jumpIf.FixedGas, Is.EqualTo(GasCostOf.JumpI));
            Assert.That((jumpIf.StackInputs, jumpIf.StackGrowth), Is.EqualTo((2, -2)));
            Assert.That(add.HasCheckedBody, Is.True);
            Assert.That(add.FixedGas, Is.EqualTo(GasCostOf.VeryLow));
            Assert.That((add.StackInputs, add.StackGrowth), Is.EqualTo((2, -1)));
            Assert.That(add.BodyType, Is.Not.Null);
        }
    }

    [TestCaseSource(nameof(Forks))]
    public void Marks_undefined_opcodes(IReleaseSpec spec)
    {
        OpcodeBody unassigned = Of(spec).Bodies[0x0c];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unassigned.IsDefined, Is.False, "0x0c is never assigned");
            Assert.That(unassigned.Flow, Is.EqualTo(OpcodeFlow.Terminates));
        }
    }

    [Test]
    public void Forks_with_the_same_opcodes_share_one_set()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Of(MuirGlacier.Instance), Is.SameAs(Of(Istanbul.Instance)), "Muir Glacier changes only the difficulty bomb");
            Assert.That(Of(GrayGlacier.Instance), Is.SameAs(Of(London.Instance)), "Gray Glacier changes only the difficulty bomb");
            Assert.That(Of(Byzantium.Instance), Is.Not.SameAs(Of(Constantinople.Instance)), "Constantinople adds the shifts");
        }
    }

    [Test]
    public void Describes_the_shifts_from_constantinople()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Of(Byzantium.Instance).Bodies[(int)Instruction.SHL].IsDefined, Is.False);
            Assert.That(Of(Constantinople.Instance).Bodies[(int)Instruction.SHL].IsDefined, Is.True);
        }
    }
}
