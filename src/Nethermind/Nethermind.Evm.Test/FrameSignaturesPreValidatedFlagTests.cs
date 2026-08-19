// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.Evm.TransactionProcessing;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Guards the blast radius of <see cref="ExecutionOptions.FrameSignaturesPreValidated"/>: it asserts a
/// precondition only the transaction pool establishes, so a second production site would be a footgun.
/// </summary>
[TestFixture]
public class FrameSignaturesPreValidatedFlagTests
{
    private const string Flag = nameof(ExecutionOptions.FrameSignaturesPreValidated);

    /// <summary>Where the flag is declared, read, and set — every other production mention is a regression.</summary>
    private static readonly string[] ExpectedSites =
    [
        "Nethermind.Evm/TransactionProcessing/ExecutionOptions.cs",
        "Nethermind.Evm/TransactionProcessing/TransactionProcessorBase.FrameTx.cs",
        "Nethermind.Consensus/Processing/FrameTxPrefixSimulator.cs",
    ];

    [Test]
    public void TheFlagIsSetOnlyWhereThePoolHasAlreadyVerifiedTheSignatures()
    {
        DirectoryInfo root = SourceRoot();
        List<string> found = [];

        foreach (string path in Directory.EnumerateFiles(root.FullName, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root.FullName, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Contains(".Test/", StringComparison.Ordinal) || relative.StartsWith("artifacts/", StringComparison.Ordinal)) continue;
            if (File.ReadAllText(path).Contains(Flag, StringComparison.Ordinal)) found.Add(relative);
        }

        Assert.That(found.Order(), Is.EquivalentTo(ExpectedSites.Order()));
    }

    private static DirectoryInfo SourceRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Nethermind.slnx")))
        {
            directory = directory.Parent;
        }

        // Failing rather than ignoring: a guard that quietly stops scanning stops guarding.
        Assert.That(directory, Is.Not.Null, "could not locate the source root to scan");
        return directory!;
    }
}
