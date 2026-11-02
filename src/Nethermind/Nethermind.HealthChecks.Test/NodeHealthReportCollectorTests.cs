// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace Nethermind.HealthChecks.Test;

public class NodeHealthReportCollectorTests
{
    // The setting decides whether the library collector, with its notify-once policy, runs instead of the in-process one.
    [Test]
    public void Notify_once_setting_reads_as_the_configuration_binder_does([Values(null, "true", "True", "false", " FALSE ", "", "yes")] string value)
    {
        IConfiguration configuration = Configuration(value);
        bool? expected = Read(() => configuration.GetSection("HealthChecksUI").GetValue<bool>("NotifyUnHealthyOneTimeUntilChange"));
        bool? actual = Read(() => NodeHealthReportCollector.NotifiesOnceUntilChange(configuration));

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void Notify_once_setting_is_read_from_the_legacy_section_name()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string> { ["HealthChecks-UI:NotifyUnHealthyOneTimeUntilChange"] = "true" })
            .Build();

        Assert.That(NodeHealthReportCollector.NotifiesOnceUntilChange(configuration), Is.True);
    }

    private static IConfiguration Configuration(string value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(value is null ? [] : new Dictionary<string, string> { ["HealthChecksUI:NotifyUnHealthyOneTimeUntilChange"] = value })
            .Build();

    // An unreadable value fails the read either way; null stands for that outcome.
    private static bool? Read(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
