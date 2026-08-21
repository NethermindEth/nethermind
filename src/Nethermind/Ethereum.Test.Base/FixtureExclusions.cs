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
        EthereumTest test, string? sourceCategory, bool isGeneratedEip7610Fixture) =>
        IsLegacyStorageCollisionTest(test, sourceCategory) ||
        isGeneratedEip7610Fixture && HasStorageWithoutEip684Collision(test);

    private static bool IsLegacyStorageCollisionTest(EthereumTest test, string? sourceCategory) =>
        (sourceCategory ?? Path.GetFileName(test.Category), GetFixtureName(test.Name)) switch
        {
            ("stCreate2", "create2collisionStorage") or
            ("stCreate2", "RevertInCreateInInitCreate2") or
            ("stSStoreTest", "InitCollision") or
            ("stExtCodeHash", "dynamicAccountOverwriteEmpty") or
            ("stRevertTest", "RevertInCreateInInit") => true,
            _ => false,
        };

    private static bool IsGeneratedEip7610Fixture(DirectoryInfo? sourceDirectory)
    {
        for (DirectoryInfo? directory = sourceDirectory; directory is not null; directory = directory.Parent)
        {
            if (directory.Name.Equals(Eip7610FixtureCategory, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool HasStorageWithoutEip684Collision(EthereumTest test)
    {
        IEnumerable<AccountState>? accounts = test switch
        {
            GeneralStateTest { Pre: not null } stateTest => stateTest.Pre.Values,
            BlockchainTest { Pre: not null } blockchainTest => blockchainTest.Pre.Values,
            _ => null,
        };

        if (accounts is null)
            return false;

        foreach (AccountState account in accounts)
        {
            if (account.Storage.Count > 0 && account.Nonce == 0 && account.Code.Length == 0)
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
