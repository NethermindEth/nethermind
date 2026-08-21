// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;

namespace Ethereum.Test.Base;

/// <summary>
/// Excludes legacy fixtures whose expected results still encode the retired storage-only collision rule.
/// </summary>
public static class LegacyFixtureExclusions
{
    private static readonly string[] RetiredStorageCollisionTests =
    [
        "stCreate2.create2collisionStorage",
        "stCreate2.RevertInCreateInInitCreate2",
        "stSStoreTest.InitCollision",
        "stExtCodeHash.dynamicAccountOverwriteEmpty",
        "stRevertTest.RevertInCreateInInit",
    ];

    /// <summary>Removes legacy collision cases that are no longer valid for the client.</summary>
    public static IEnumerable<T> Filter<T>(IEnumerable<T> tests) where T : EthereumTest
    {
        foreach (T test in tests)
        {
            if (!IsRetiredStorageCollisionTest(test))
            {
                yield return test;
            }
        }
    }

    private static bool IsRetiredStorageCollisionTest(EthereumTest test)
    {
        string testIdentifier = $"{Path.GetFileName(test.Category)}.{test.Name}";
        foreach (string pattern in RetiredStorageCollisionTests)
        {
            if (testIdentifier.StartsWith(pattern, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
