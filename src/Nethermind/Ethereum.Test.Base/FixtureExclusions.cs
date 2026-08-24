// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;

namespace Ethereum.Test.Base;

/// <summary>
/// Excludes fixtures whose expected results still encode the retired storage-only CREATE collision rule.
/// </summary>
public static class FixtureExclusions
{
    private const string Eip7610FixtureCategory = "eip7610_create_collision";

    /// <summary>Removes collision cases that are no longer valid for the client.</summary>
    public static IEnumerable<T> Filter<T>(IEnumerable<T> tests, string? sourceFile = null) where T : EthereumTest
    {
        DirectoryInfo? sourceDirectory = sourceFile is null ? null : new FileInfo(sourceFile).Directory;
        bool isGeneratedEip7610Fixture = IsGeneratedEip7610Fixture(sourceDirectory);
        foreach (T test in tests)
        {
            if (!IsRetiredStorageCollisionTest(test, sourceDirectory?.Name, isGeneratedEip7610Fixture))
                yield return test;
        }
    }

    private static bool IsRetiredStorageCollisionTest(
        EthereumTest test, string? sourceCategory, bool isGeneratedEip7610Fixture)
    {
        sourceCategory ??= Path.GetFileName(test.Category);
        string fixtureName = GetFixtureName(test.Name);
        return IsLegacyStorageCollisionTest(fixtureName, sourceCategory) ||
            isGeneratedEip7610Fixture && IsGeneratedStorageCollisionTest(fixtureName);
    }

    private static bool IsLegacyStorageCollisionTest(string fixtureName, string? sourceCategory) =>
        (sourceCategory, fixtureName) switch
        {
            ("stCreate2", "create2collisionStorage") or
            ("stCreate2", "RevertInCreateInInitCreate2") or
            ("stSStoreTest", "InitCollision") or
            ("stExtCodeHash", "dynamicAccountOverwriteEmpty") or
            ("stRevertTest", "RevertInCreateInInit") => true,
            _ => false,
        };

    private static bool IsGeneratedStorageCollisionTest(string fixtureName) => fixtureName is
        "test_create2_collision_storage[fork_Amsterdam-state_test-empty-initcode]" or
        "test_create2_collision_storage[fork_Amsterdam-state_test-initcode-with-deploy]" or
        "test_create2_collision_storage[fork_Amsterdam-state_test-sstore-initcode]" or
        "test_create2_collision_storage[fork_Paris-state_test-empty-initcode]" or
        "test_create2_collision_storage[fork_Paris-state_test-initcode-with-deploy]" or
        "test_create2_collision_storage[fork_Paris-state_test-sstore-initcode]" or
        "test_init_collision_create_opcode[fork_Cancun-state_test-opcode_CREATE-non-empty-balance-correct-initcode]" or
        "test_init_collision_create_opcode[fork_Cancun-state_test-opcode_CREATE2-non-empty-balance-correct-initcode]" or
        "test_init_collision_create_opcode[fork_ConstantinopleFix-state_test-opcode_CREATE-non-empty-balance-correct-initcode]" or
        "test_init_collision_create_opcode[fork_ConstantinopleFix-state_test-opcode_CREATE2-non-empty-balance-correct-initcode]" or
        "test_init_collision_create_opcode[fork_Osaka-state_test-opcode_CREATE-non-empty-balance-correct-initcode]" or
        "test_init_collision_create_opcode[fork_Osaka-state_test-opcode_CREATE2-non-empty-balance-correct-initcode]" or
        "test_init_collision_create_opcode[fork_Prague-state_test-opcode_CREATE-non-empty-balance-correct-initcode]" or
        "test_init_collision_create_opcode[fork_Prague-state_test-opcode_CREATE2-non-empty-balance-correct-initcode]" or
        "test_init_collision_create_tx[fork_Berlin-tx_type_0-state_test-non-empty-balance-correct-initcode]" or
        "test_init_collision_create_tx[fork_Berlin-tx_type_0-state_test-non-empty-balance-revert-initcode]" or
        "test_init_collision_create_tx[fork_Berlin-tx_type_1-state_test-non-empty-balance-correct-initcode]" or
        "test_init_collision_create_tx[fork_Berlin-tx_type_1-state_test-non-empty-balance-revert-initcode]" or
        "test_init_collision_create_tx[fork_Frontier-tx_type_0-state_test-non-empty-balance-correct-initcode]" or
        "test_init_collision_create_tx[fork_Homestead-tx_type_0-state_test-non-empty-balance-correct-initcode]" or
        "test_init_collision_create_tx[fork_Shanghai-tx_type_0-state_test-non-empty-balance-correct-initcode]" or
        "test_init_collision_create_tx[fork_Shanghai-tx_type_0-state_test-non-empty-balance-revert-initcode]" or
        "test_init_collision_create_tx[fork_Shanghai-tx_type_1-state_test-non-empty-balance-correct-initcode]" or
        "test_init_collision_create_tx[fork_Shanghai-tx_type_1-state_test-non-empty-balance-revert-initcode]" or
        "test_init_collision_create_tx[fork_Shanghai-tx_type_2-state_test-non-empty-balance-correct-initcode]" or
        "test_init_collision_create_tx[fork_Shanghai-tx_type_2-state_test-non-empty-balance-revert-initcode]";

    private static bool IsGeneratedEip7610Fixture(DirectoryInfo? sourceDirectory)
    {
        for (DirectoryInfo? directory = sourceDirectory; directory is not null; directory = directory.Parent)
        {
            if (directory.Name.Equals(Eip7610FixtureCategory, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string GetFixtureName(string? testName)
    {
        if (testName is null)
            return string.Empty;

        int caseSuffix = testName.IndexOf("_d", StringComparison.Ordinal);
        string fixtureName = caseSuffix >= 0 ? testName[..caseSuffix] : testName;
        if (fixtureName.EndsWith("Paris", StringComparison.Ordinal))
            fixtureName = fixtureName[..^"Paris".Length].TrimEnd('_');

        return fixtureName;
    }
}
