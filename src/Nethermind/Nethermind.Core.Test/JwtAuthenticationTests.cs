// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Linq;
using Nethermind.Core.Authentication;
using Nethermind.Core.Test.IO;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class JwtAuthenticationTests
{
    [Test]
    public void FromFile_logs_when_secret_is_automatically_created()
    {
        using TempPath tempDirectory = TempPath.GetTempDirectory();
        string secretPath = Path.Combine(tempDirectory.Path, "jwt.hex");
        TestLogger testLogger = new();

        JwtAuthentication.FromFile(secretPath, Timestamper.Default, new ILogger(testLogger));

        string secret = File.ReadAllText(secretPath);
        bool hasCreatedLog = testLogger.LogList.Any(log =>
            log.Contains(secretPath) && log.Contains("automatically created"));

        Assert.That(secret, Has.Length.EqualTo(64));
        Assert.That(secret, Does.Match("^[0-9a-fA-F]{64}$"));
        Assert.That(hasCreatedLog, Is.True);
    }
}
