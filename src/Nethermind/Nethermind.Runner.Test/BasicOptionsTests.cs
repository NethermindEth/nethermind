// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using System.Linq;
using Nethermind.Config;
using Nethermind.Core.Test;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

public class BasicOptionsTests
{
    [TestCase(new[] { "--Init.MemoryHint", "4294967296" }, "--Init.MemoryHint")]
    [TestCase(new[] { "--Init.MemoryHint=4294967296" }, "--Init.MemoryHint=4294967296")]
    [TestCase(new[] { "--Db.SkipMemoryHintSetting" }, "--Db.SkipMemoryHintSetting")]
    [TestCase(new[] { "-x" }, "-x")]
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

    [TestCase(new[] { "--Init.MemoryHint", "4294967296" }, "Unrecognized option '--Init.MemoryHint'.", ExitCodes.UnrecognizedOption, true)]
    [TestCase(new[] { "verify-trie", "--bogus" }, "Unrecognized command or argument '--bogus'.", ExitCodes.UnrecognizedOption, true)]
    [TestCase(new[] { "verify-trie", "extra" }, "Unrecognized command or argument 'extra'.", ExitCodes.UnrecognizedOption, false)]
    [TestCase(new[] { "--force-resync", "--purge-db" }, "Cannot use --force-resync and --purge-db together. Choose one.", ExitCodes.GeneralError, false)]
    public void Parse_errors_are_reported(string[] args, string firstError, int exitCode, bool releaseNotesHint)
    {
        TestLogger logger = new();
        ParseResult result = new RootCommand { BasicOptions.Command, BasicOptions.ForceResync, BasicOptions.PurgeDb }.Parse(args);

        int code = BasicOptions.ReportParseErrors(result, new ILogger(logger));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(code, Is.EqualTo(exitCode));
            Assert.That(logger.LogList[0], Is.EqualTo(firstError));
            Assert.That(logger.LogList[^1], releaseNotesHint ? Does.Contain("release notes") : Does.Not.Contain("release notes"));
        }
    }
}
