// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;

namespace Ethereum.Test.Base;

internal static class FixtureExclusions
{
    private static readonly Dictionary<string, string[]> ExcludedFixturePatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["stCreate2"] =
        [
            "create2collisionStorage*",
            "RevertInCreateInInitCreate2*",
        ],
        ["stSStoreTest"] =
        [
            "InitCollision",
            "InitCollisionParis",
        ],
        ["stExtCodeHash"] = ["dynamicAccountOverwriteEmpty*"],
        ["stRevertTest"] = ["RevertInCreateInInit*"],
        ["stSpecialTest"] = ["FailedCreateRevertsDeletionParis"],
        ["eip7610_create_collision"] =
        [
            "test_collision_with_create2_revert_in_initcode[fork_*",
            "test_create*_collision_storage[fork_*",
            "test_init_collision_create_opcode[fork_*-opcode_*-non-empty-balance-correct-initcode]*",
            "test_init_collision_create_tx[fork_*-non-empty-balance-correct-initcode]*",
            "test_init_collision_create_tx[fork_*-non-empty-balance-revert-initcode]*",
        ],
    };

    public static IEnumerable<T> Filter<T>(IEnumerable<T> tests, string? sourceFile = null) where T : EthereumTest
    {
        DirectoryInfo? sourceDirectory = sourceFile is null ? null : new FileInfo(sourceFile).Directory;
        foreach (T test in tests)
        {
            if (!IsExcluded(test, sourceDirectory))
                yield return test;
        }
    }

    private static bool IsExcluded(EthereumTest test, DirectoryInfo? sourceDirectory)
    {
        string fixtureName = GetFixtureName(test.Name);
        for (DirectoryInfo? directory = sourceDirectory; directory is not null; directory = directory.Parent)
        {
            if (IsExcluded(directory.Name, fixtureName))
                return true;
        }

        return IsExcluded(Path.GetFileName(test.Category), fixtureName);
    }

    private static bool IsExcluded(string? category, string fixtureName)
    {
        if (category is null || !ExcludedFixturePatterns.TryGetValue(category, out string[]? patterns))
            return false;

        foreach (string pattern in patterns)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, fixtureName))
                return true;
        }

        return false;
    }

    private static string GetFixtureName(string? testName)
    {
        if (testName is null)
            return string.Empty;

        int caseSuffix = testName.LastIndexOf("_d", StringComparison.Ordinal);
        return caseSuffix >= 0 ? testName[..caseSuffix] : testName;
    }
}
