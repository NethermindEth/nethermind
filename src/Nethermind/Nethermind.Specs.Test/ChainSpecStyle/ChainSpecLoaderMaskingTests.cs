// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Specs.Test.ChainSpecStyle;

/// <remarks>A non-parallel fixture of its own, as the test sets the process-wide masking flag that the log output of other tests depends on.</remarks>
[NonParallelizable]
public class ChainSpecLoaderMaskingTests
{
    [Test]
    public void File_diagnostics_follow_sensitive_masking([Values] bool maskSensitiveData)
    {
        string path = Path.Combine(Path.GetTempPath(), $"private-chain-spec-{Guid.NewGuid():N}.json");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Specs", "genesis_test.json"), path);
        bool previous = SensitiveLogMasking.Enabled;
        SensitiveLogMasking.Enabled = maskSensitiveData;
        try
        {
            InterfaceLogger logger = Substitute.For<InterfaceLogger>();
            logger.IsInfo.Returns(true);
            List<string> lines = [];
            logger.When(log => log.Info(Arg.Any<string>())).Do(call => lines.Add(call.Arg<string>()));
            ChainSpecFileLoader loader = new(new EthereumJsonSerializer(), new OneLoggerLogManager(new(logger)));

            ChainSpec chainSpec = loader.LoadEmbeddedOrFromFile(path);

            string displayedPath = maskSensitiveData ? "[redacted]" : path;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(chainSpec.ChainId, Is.EqualTo(22082));
                Assert.That(lines, Does.Contain($"Did not find ChainSpec in embedded resources: {displayedPath}"));
                Assert.That(lines, Does.Contain($"Loading ChainSpec from file: {displayedPath}"));
                if (maskSensitiveData) Assert.That(lines, Has.None.Contains(path));
            }
        }
        finally
        {
            SensitiveLogMasking.Enabled = previous;
            File.Delete(path);
        }
    }
}
