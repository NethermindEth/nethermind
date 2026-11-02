// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Tools.ProfilingMcp.DotTrace;
using NUnit.Framework;

namespace Nethermind.Tools.ProfilingMcp.Test;

public class ServiceMessageTests
{
    [TestCase("\uFEFF##dotTrace[\"ready\"]", "ready", null, null)]
    [TestCase("##dotTrace[\"connected\",{\"pid\":19016,\"path\":\"C:\\\\app\\\\App.exe\"}]", "connected", 19016, @"C:\app\App.exe")]
    [TestCase("\uFEFF##dotTrace[\"snapshot-saved\",{\"pid\":7,\"filename\":\"/tmp/s.dtp\"}]  ", "snapshot-saved", 7, "/tmp/s.dtp")]
    public void Parses_profiler_output(string line, string name, int? pid, string? path)
    {
        bool parsed = ServiceMessage.TryParse(line, out ServiceMessage message);
        Assert.That((parsed, message), Is.EqualTo((true, new ServiceMessage(name, pid, path))));
    }

    [TestCase("Profiling in progress...")]
    [TestCase("##dotTrace[")]
    [TestCase("##dotTrace[42]")]
    [TestCase("")]
    public void Ignores_other_output(string line) =>
        Assert.That(ServiceMessage.TryParse(line, out _), Is.False);

    [TestCase("get-snapshot", null, "\r\n##dotTrace[\"get-snapshot\"]\r\n")]
    [TestCase("disconnect", 1234, "\r\n##dotTrace[\"disconnect\",{\"pid\":1234}]\r\n")]
    public void Formats_commands_on_their_own_line(string name, int? pid, string expected) =>
        Assert.That(ServiceMessage.FormatCommand(name, pid), Is.EqualTo(expected));
}
