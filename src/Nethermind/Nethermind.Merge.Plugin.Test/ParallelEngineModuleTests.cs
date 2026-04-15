// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>
/// Runs all engine module tests with an explicit parallel execution setting.
/// The <c>[TestFixture(false)]</c> variant forces sequential BAL validation;
/// <c>[TestFixture(true)]</c> forces parallel BAL validation.
/// Both must produce identical block hashes, state roots, and receipts roots.
/// Pre-Amsterdam tests (V1-V4) are unaffected by the flag but run for safety.
/// </summary>
[TestFixture(false)]
[TestFixture(true)]
[Parallelizable(ParallelScope.All)]
public class ParallelEngineModuleTests(bool parallel) : EngineModuleTests
{
    protected override MergeTestBlockchain CreateBaseBlockchain(
        IMergeConfig? mergeConfig = null)
    {
        MergeTestBlockchain bc = base.CreateBaseBlockchain(mergeConfig);
        bc.ParallelExecutionOverride = parallel;
        return bc;
    }
}
