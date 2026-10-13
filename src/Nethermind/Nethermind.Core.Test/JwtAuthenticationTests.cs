// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Core.Authentication;
using Nethermind.Core.Test.IO;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class JwtAuthenticationTests
{
    [Test]
    public async Task Library_authentication_trace_does_not_log_bearer_token()
    {
        const string secret = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        byte[] secretBytes = Convert.FromHexString(secret);
        string token = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            IssuedAt = Timestamper.Default.UtcNow,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(secretBytes), SecurityAlgorithms.HmacSha256),
            AdditionalHeaderClaims = new Dictionary<string, object> { ["probe"] = "fallback" }
        });
        TestLogger logger = new();
        JwtAuthentication authentication = JwtAuthentication.FromSecret(secret, Timestamper.Default, new ILogger(logger));

        Assert.That(await authentication.Authenticate("Bearer " + token), Is.True);
        string trace = logger.LogList.Single(log => log.Contains("Message authenticated"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(trace, Does.Contain("iat:").And.Contain("time:"));
            Assert.That(trace, Does.Not.Contain(token));
        }
    }

    [Test]
    public void Authenticators_on_the_same_thread_do_not_share_signing_keys()
    {
        JwtAuthentication first = JwtAuthentication.FromSecret(new string('a', 64), Timestamper.Default, NullLogger.Instance);
        JwtAuthentication second = JwtAuthentication.FromSecret(new string('b', 64), Timestamper.Default, NullLogger.Instance);
        string firstToken = "Bearer " + first.CreateWarmupToken();
        string secondToken = "Bearer " + second.CreateWarmupToken();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Authenticate(firstToken).GetAwaiter().GetResult(), Is.True);
            Assert.That(second.Authenticate(firstToken).GetAwaiter().GetResult(), Is.False);
            Assert.That(second.Authenticate(secondToken).GetAwaiter().GetResult(), Is.True);
            Assert.That(first.Authenticate(secondToken).GetAwaiter().GetResult(), Is.False);
        }
    }

    [Test]
    public async Task Warmup_token_uses_the_loaded_secret_after_the_file_is_removed()
    {
        using TempPath secretPath = TempPath.GetTempFile();
        await File.WriteAllTextAsync(secretPath.Path, new string('a', 64));
        JwtAuthentication authentication = JwtAuthentication.FromFile(secretPath.Path, Timestamper.Default, NullLogger.Instance);
        File.Delete(secretPath.Path);

        Assert.That(await authentication.Authenticate("Bearer " + authentication.CreateWarmupToken()), Is.True);
    }

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

    [Test]
    [NonParallelizable]
    public void FromFile_masks_secret_path_when_enabled()
    {
        using TempPath tempDirectory = TempPath.GetTempDirectory();
        string secretPath = Path.Combine(tempDirectory.Path, "jwt.hex");
        TestLogger testLogger = new();
        bool originalMasking = SensitiveLogMasking.Enabled;
        SensitiveLogMasking.Enabled = true;
        try
        {
            JwtAuthentication.FromFile(secretPath, Timestamper.Default, new ILogger(testLogger));
            JwtAuthentication.FromFile(secretPath, Timestamper.Default, new ILogger(testLogger));
            File.WriteAllText(secretPath, "invalid");
            Assert.Throws<FormatException>(() => JwtAuthentication.FromFile(secretPath, Timestamper.Default, new ILogger(testLogger)));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(testLogger.LogList, Has.Some.Contains("automatically created"));
                Assert.That(testLogger.LogList, Has.Some.Contains("Reading authentication secret"));
                Assert.That(testLogger.LogList, Has.Some.Contains("not a 64-digit hex number"));
                Assert.That(testLogger.LogList, Has.None.Contains(secretPath));
                Assert.That(testLogger.LogList.Count(log => log.Contains("[redacted]")), Is.EqualTo(4));
            }
        }
        finally
        {
            SensitiveLogMasking.Enabled = originalMasking;
        }
    }

    [Test]
    [NonParallelizable]
    public void FromFile_masks_path_in_file_error([Values] bool readFailure)
    {
        using TempPath tempDirectory = TempPath.GetTempDirectory();
        string secretPath = Path.Combine(tempDirectory.Path, "jwt.hex");
        Directory.CreateDirectory(tempDirectory.Path);
        if (readFailure) File.WriteAllText(secretPath, new string('a', 64));
        else Directory.CreateDirectory(secretPath);
        using FileStream? exclusive = readFailure ? new FileStream(secretPath, FileMode.Open, FileAccess.Read, FileShare.None) : null;
        TestErrorLogManager logManager = new();
        bool originalMasking = SensitiveLogMasking.Enabled;
        SensitiveLogMasking.Enabled = true;
        try
        {
            Assert.That(() => JwtAuthentication.FromFile(secretPath, Timestamper.Default, logManager.GetLogger("test")),
                Throws.InstanceOf<SystemException>());

            TestErrorLogManager.Error error = logManager.Errors.Single();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(error.Text, Does.Contain("[redacted]").And.Contain("Exception:").And.Not.Contain(secretPath));
                Assert.That(error.Exception, Is.Null);
            }
        }
        finally
        {
            SensitiveLogMasking.Enabled = originalMasking;
        }
    }
}
