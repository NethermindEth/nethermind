// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using System.Linq;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

public class BasicOptionsTests
{
    [TestCase(new[] { "--Init.MemoryHint", "4294967296" }, "--Init.MemoryHint")]
    [TestCase(new[] { "--Init.MemoryHint=4294967296" }, "--Init.MemoryHint=4294967296")]
    [TestCase(new[] { "--Db.SkipMemoryHintSetting" }, "--Db.SkipMemoryHintSetting")]
    public void Unrecognized_option_is_not_taken_as_command(string[] args, string option)
    {
        ParseResult result = new RootCommand { BasicOptions.Command }.Parse(args);

        Assert.That(result.Errors.Select(static e => e.Message), Does.Contain($"Unrecognized option '{option}'."));
    }

    [Test]
    public void Command_is_parsed()
    {
        ParseResult result = new RootCommand { BasicOptions.Command }.Parse(["verify-trie"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Errors, Is.Empty);
            Assert.That(result.GetValue(BasicOptions.Command), Is.EqualTo("verify-trie"));
        }
    }
}
