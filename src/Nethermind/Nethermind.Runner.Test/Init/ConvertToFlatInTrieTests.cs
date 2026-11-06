// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Config;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.State.Flat;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Init;

public class ConvertToFlatInTrieTests
{
    [Test]
    public void Execute_WithoutFlatInTrieConfigured_Fails([Values(FlatLayout.Flat, FlatLayout.PreimageFlat)] FlatLayout layout)
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        ConvertToFlatInTrie step = new(new FlatInTrieConverter(db, LimboLogs.Instance), new FlatDbConfig { Layout = layout }, LimboLogs.Instance);

        Assert.That(async () => await step.Execute(CancellationToken.None),
            Throws.TypeOf<InvalidConfigurationException>()
                .With.Property(nameof(InvalidConfigurationException.ExitCode)).EqualTo(ExitCodes.ForbiddenOptionValue));
    }
}
