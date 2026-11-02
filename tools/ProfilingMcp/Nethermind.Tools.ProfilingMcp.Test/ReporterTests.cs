// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Xml.Linq;
using Nethermind.Tools.ProfilingMcp.DotTrace;
using NUnit.Framework;

namespace Nethermind.Tools.ProfilingMcp.Test;

public class ReporterTests
{
    [Test]
    public void Parses_report_with_call_stacks()
    {
        const string xml = """
            <Report>
              <Info><Snapshot IndexFile="s.dtp" Executable="Demo.exe" CommandLine="Demo.exe 5" /></Info>
              <Function Id="0x00200002" FQN="Program.SlowHash" TotalTime="285" OwnTime="27" Samples="52" Instances="1">
                <Instance CallStack="Program.Main/Program.SlowHash" TotalTime="285" OwnTime="27" Samples="52" />
              </Function>
              <Function Id="0x00000002" FQN="[Garbage collection]" TotalTime="89" OwnTime="89" Samples="11" Instances="1">
                <Instance TotalTime="89" OwnTime="89" Samples="11" />
              </Function>
            </Report>
            """;

        IReadOnlyList<FunctionStat> functions = Reporter.Parse(XDocument.Parse(xml));

        Assert.That(functions, Has.Count.EqualTo(2));
        FunctionStat slowHash = functions[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That((slowHash.Name, slowHash.TotalTime, slowHash.OwnTime, slowHash.Samples, slowHash.Calls), Is.EqualTo(("Program.SlowHash", 285d, 27d, (long?)52, (long?)null)));
            Assert.That(slowHash.CallStacks.Single().Frames, Is.EqualTo(new[] { "Program.Main", "Program.SlowHash" }));
            Assert.That(functions[1].CallStacks, Is.Empty, "instances without a call stack attribute are aggregates, not stacks");
        }
    }

    [Test]
    public void Parses_signed_comparison_deltas()
    {
        const string xml = """
            <Report>
              <Function FQN="A.Faster" TotalTime="-12.5" OwnTime="-3" Calls="-4" />
              <Function FQN="B.Slower" TotalTime="+1234.0" OwnTime="+7.25" Calls="+2" />
            </Report>
            """;

        IReadOnlyList<FunctionStat> deltas = Reporter.Parse(XDocument.Parse(xml));

        Assert.That(deltas.Select(d => (d.TotalTime, d.OwnTime, d.Calls)), Is.EqualTo(new[] { (-12.5, -3d, (long?)-4), (1234d, 7.25, (long?)2) }));
    }
}
