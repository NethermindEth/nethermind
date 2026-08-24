// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;

namespace Ethereum.Test.Base;

internal static class FixtureExclusions
{
    private static readonly Dictionary<string, string[]> ExcludedFixtureNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["stCreate2"] =
        [
            "create2collisionStorage",
            "create2collisionStorageParis",
            "RevertInCreateInInitCreate2",
        ],
        ["stSStoreTest"] =
        [
            "InitCollision",
            "InitCollisionParis",
        ],
        ["stExtCodeHash"] = ["dynamicAccountOverwriteEmpty_Paris"],
        ["stRevertTest"] = ["RevertInCreateInInit_Paris"],
        ["stSpecialTest"] = ["FailedCreateRevertsDeletionParis"],
        ["eip7610_create_collision"] =
        [
            "test_create2_collision_storage[fork_Amsterdam-state_test-empty-initcode]",
            "test_create2_collision_storage[fork_Amsterdam-state_test-initcode-with-deploy]",
            "test_create2_collision_storage[fork_Amsterdam-state_test-sstore-initcode]",
            "test_create2_collision_storage[fork_Paris-state_test-empty-initcode]",
            "test_create2_collision_storage[fork_Paris-state_test-initcode-with-deploy]",
            "test_create2_collision_storage[fork_Paris-state_test-sstore-initcode]",
            "test_init_collision_create_opcode[fork_Cancun-state_test-opcode_CREATE-non-empty-balance-correct-initcode]",
            "test_init_collision_create_opcode[fork_Cancun-state_test-opcode_CREATE2-non-empty-balance-correct-initcode]",
            "test_init_collision_create_opcode[fork_ConstantinopleFix-state_test-opcode_CREATE-non-empty-balance-correct-initcode]",
            "test_init_collision_create_opcode[fork_ConstantinopleFix-state_test-opcode_CREATE2-non-empty-balance-correct-initcode]",
            "test_init_collision_create_opcode[fork_Osaka-state_test-opcode_CREATE-non-empty-balance-correct-initcode]",
            "test_init_collision_create_opcode[fork_Osaka-state_test-opcode_CREATE2-non-empty-balance-correct-initcode]",
            "test_init_collision_create_opcode[fork_Prague-state_test-opcode_CREATE-non-empty-balance-correct-initcode]",
            "test_init_collision_create_opcode[fork_Prague-state_test-opcode_CREATE2-non-empty-balance-correct-initcode]",
            "test_init_collision_create_tx[fork_Berlin-tx_type_0-state_test-non-empty-balance-correct-initcode]",
            "test_init_collision_create_tx[fork_Berlin-tx_type_0-state_test-non-empty-balance-revert-initcode]",
            "test_init_collision_create_tx[fork_Berlin-tx_type_1-state_test-non-empty-balance-correct-initcode]",
            "test_init_collision_create_tx[fork_Berlin-tx_type_1-state_test-non-empty-balance-revert-initcode]",
            "test_init_collision_create_tx[fork_Frontier-tx_type_0-state_test-non-empty-balance-correct-initcode]",
            "test_init_collision_create_tx[fork_Homestead-tx_type_0-state_test-non-empty-balance-correct-initcode]",
            "test_init_collision_create_tx[fork_Shanghai-tx_type_0-state_test-non-empty-balance-correct-initcode]",
            "test_init_collision_create_tx[fork_Shanghai-tx_type_0-state_test-non-empty-balance-revert-initcode]",
            "test_init_collision_create_tx[fork_Shanghai-tx_type_1-state_test-non-empty-balance-correct-initcode]",
            "test_init_collision_create_tx[fork_Shanghai-tx_type_1-state_test-non-empty-balance-revert-initcode]",
            "test_init_collision_create_tx[fork_Shanghai-tx_type_2-state_test-non-empty-balance-correct-initcode]",
            "test_init_collision_create_tx[fork_Shanghai-tx_type_2-state_test-non-empty-balance-revert-initcode]",
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
        if (category is null || !ExcludedFixtureNames.TryGetValue(category, out string[]? names))
            return false;

        foreach (string name in names)
        {
            if (fixtureName.Equals(name, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string GetFixtureName(string? testName)
    {
        if (testName is null)
            return string.Empty;

        int caseSuffix = testName.IndexOf("_d", StringComparison.Ordinal);
        return caseSuffix >= 0 ? testName[..caseSuffix] : testName;
    }
}
