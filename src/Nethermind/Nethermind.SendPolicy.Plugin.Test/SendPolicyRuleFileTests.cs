// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.IO;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.SendPolicy.Plugin.Test;

[Parallelizable(ParallelScope.All)]
public class SendPolicyRuleFileTests
{
    private static readonly Address Sender = new("0x1111111111111111111111111111111111111111");
    private static readonly Address PreviousDestination = new("0x2222222222222222222222222222222222222222");
    private static readonly Address NewDestination = new("0x3333333333333333333333333333333333333333");

    [Test]
    public void Same_length_and_timestamp_replacement_updates_guarded_destinations()
    {
        using TempPath directory = TempPath.GetTempDirectory();
        Directory.CreateDirectory(directory.Path);
        string path = Path.Combine(directory.Path, "rules");
        File.WriteAllLines(path, [ $"from {Sender}", $"to {PreviousDestination} 1" ]);
        long size = new FileInfo(path).Length;
        DateTime timestamp = File.GetLastWriteTimeUtc(path);

        SendPolicyRuleFile file = new(new SendPolicyConfig { Enabled = true, RulesPath = path }, LimboLogs.Instance);
        Assert.That(file.Current.Destinations.ContainsKey(PreviousDestination), Is.True);
        Assert.That(file.Current.Destinations.ContainsKey(NewDestination), Is.False);

        File.WriteAllLines(path, [ $"from {Sender}", $"to {NewDestination} 1" ]);
        File.SetLastWriteTimeUtc(path, timestamp);
        Assert.That(new FileInfo(path).Length, Is.EqualTo(size), "the cached length must not change");
        Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(timestamp), "the cached timestamp must not change");

        Assert.That(file.Current.Destinations.ContainsKey(PreviousDestination), Is.False);
        Assert.That(file.Current.Destinations.ContainsKey(NewDestination), Is.True);
    }

    [Test]
    public void Same_length_and_timestamp_malformed_replacement_refuses_previous_guarded_senders()
    {
        using TempPath directory = TempPath.GetTempDirectory();
        Directory.CreateDirectory(directory.Path);
        string path = Path.Combine(directory.Path, "rules");
        File.WriteAllLines(path, [ $"from {Sender}", $"to {PreviousDestination} 1" ]);
        long size = new FileInfo(path).Length;
        DateTime timestamp = File.GetLastWriteTimeUtc(path);

        SendPolicyRuleFile file = new(new SendPolicyConfig { Enabled = true, RulesPath = path }, LimboLogs.Instance);
        Assert.That(file.Current.Error, Is.Null);

        File.WriteAllLines(path, [ $"from {Sender}", $"zz {PreviousDestination} 1" ]);
        File.SetLastWriteTimeUtc(path, timestamp);
        Assert.That(new FileInfo(path).Length, Is.EqualTo(size));
        Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(timestamp));

        Assert.That(file.Current.Error, Does.Contain("unknown rule 'zz'"));
        Assert.That(file.Current.GuardedSenders, Does.Contain(Sender));
        Assert.That(file.Current.Destinations, Is.Empty);
    }

    [Test]
    public void Identical_content_preserves_cached_rules()
    {
        using TempPath directory = TempPath.GetTempDirectory();
        Directory.CreateDirectory(directory.Path);
        string path = Path.Combine(directory.Path, "rules");
        File.WriteAllLines(path, [ $"from {Sender}", $"to {PreviousDestination} 1" ]);

        SendPolicyRuleFile file = new(new SendPolicyConfig { Enabled = true, RulesPath = path }, LimboLogs.Instance);
        SendPolicyRules rules = file.Current;
        File.WriteAllLines(path, [ $"from {Sender}", $"to {PreviousDestination} 1" ]);

        Assert.That(file.Current, Is.SameAs(rules));
    }
}
