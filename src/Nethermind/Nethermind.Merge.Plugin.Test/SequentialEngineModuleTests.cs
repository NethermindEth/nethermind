// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>
/// Runs all engine module tests with an explicit sequential execution setting.
/// Both must produce identical block hashes, state roots, and receipts roots.
/// Pre-Amsterdam tests (V1-V4) are unaffected by the flag but run for safety.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class SequentialEngineModuleTests : EngineModuleTests
{
    protected override MergeTestBlockchain CreateBaseBlockchain(
        IMergeConfig? mergeConfig = null)
    {
        MergeTestBlockchain bc = base.CreateBaseBlockchain(mergeConfig);
        bc.ParallelExecutionOverride = false;
        return bc;
    }
}
