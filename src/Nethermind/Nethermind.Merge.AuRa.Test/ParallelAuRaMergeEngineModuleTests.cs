// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Test;
using NUnit.Framework;

namespace Nethermind.Merge.AuRa.Test;

/// <summary>
/// Runs all AuRa engine module tests with an explicit parallel execution setting.
/// Mirrors <see cref="ParallelEngineModuleTests"/> for the AuRa consensus variant.
/// Both parallel and sequential must produce identical hashes.
/// </summary>
[TestFixture(false)]
[TestFixture(true)]
[Parallelizable(ParallelScope.All)]
public class ParallelAuRaMergeEngineModuleTests(bool parallel) : AuRaMergeEngineModuleTests
{
    protected override MergeTestBlockchain CreateBaseBlockchain(
        IMergeConfig? mergeConfig = null)
    {
        MergeTestBlockchain bc = base.CreateBaseBlockchain(mergeConfig);
        bc.ParallelExecutionOverride = parallel;
        return bc;
    }
}
