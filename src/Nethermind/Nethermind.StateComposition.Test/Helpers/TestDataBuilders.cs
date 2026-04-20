// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Collections.Immutable;
using Nethermind.StateComposition.Data;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test.Helpers;

/// <summary>
/// Shared fixtures for the StateComposition test suite. Centralises the
/// <see cref="CumulativeTrieStats"/> builders and <see cref="IStateCompositionConfig"/>
/// substitute that multiple fixtures otherwise copy verbatim — keeps defaults in one
/// place so a signature change shows up as a single edit rather than a sprawl.
/// </summary>
internal static class TestDataBuilders
{
    /// <summary>
    /// Baseline with all structural counters zeroed. Optional overrides match
    /// the init-only fields on <see cref="CumulativeTrieStats"/>.
    /// </summary>
    public static CumulativeTrieStats EmptyBaseline(long codeBytes = 0, long[]? histogram = null) =>
        BuildStats(codeBytes: codeBytes, hist: ImmutableArray.Create(
            histogram ?? new long[CumulativeTrieStats.SlotHistogramLength]));

    /// <summary>
    /// Fully parameterised builder. Any field omitted defaults to zero/empty so tests
    /// only spell out the values that matter to the assertion.
    /// </summary>
    public static CumulativeTrieStats BuildStats(
        long accountsTotal = 0,
        long contractsTotal = 0,
        long storageSlotsTotal = 0,
        long accountTrieBranches = 0,
        long accountTrieExtensions = 0,
        long accountTrieLeaves = 0,
        long accountTrieBytes = 0,
        long storageTrieBranches = 0,
        long storageTrieExtensions = 0,
        long storageTrieLeaves = 0,
        long storageTrieBytes = 0,
        long contractsWithStorage = 0,
        long emptyAccounts = 0,
        long codeBytes = 0,
        ImmutableArray<long> hist = default) =>
        new(
            AccountsTotal: accountsTotal,
            ContractsTotal: contractsTotal,
            StorageSlotsTotal: storageSlotsTotal,
            AccountTrieBranches: accountTrieBranches,
            AccountTrieExtensions: accountTrieExtensions,
            AccountTrieLeaves: accountTrieLeaves,
            AccountTrieBytes: accountTrieBytes,
            StorageTrieBranches: storageTrieBranches,
            StorageTrieExtensions: storageTrieExtensions,
            StorageTrieLeaves: storageTrieLeaves,
            StorageTrieBytes: storageTrieBytes,
            ContractsWithStorage: contractsWithStorage,
            EmptyAccounts: emptyAccounts)
        {
            CodeBytesTotal = codeBytes,
            SlotCountHistogram = hist.IsDefault
                ? ImmutableArray.Create(new long[CumulativeTrieStats.SlotHistogramLength])
                : hist,
        };

    /// <summary>
    /// Mock <see cref="IStateCompositionConfig"/> with the defaults shared by every
    /// Service-level fixture. Callers can override fields via the optional params.
    /// </summary>
    public static IStateCompositionConfig CreateTestConfig(
        int scanParallelism = 4,
        long scanMemoryBudgetBytes = 1_000_000_000L,
        int scanQueueTimeoutSeconds = 5,
        int topNContracts = 20,
        bool excludeStorage = false,
        bool persistSnapshots = false,
        bool trackDepthIncrementally = false)
    {
        IStateCompositionConfig config = Substitute.For<IStateCompositionConfig>();
        config.ScanParallelism.Returns(scanParallelism);
        config.ScanMemoryBudgetBytes.Returns(scanMemoryBudgetBytes);
        config.ScanQueueTimeoutSeconds.Returns(scanQueueTimeoutSeconds);
        config.TopNContracts.Returns(topNContracts);
        config.ExcludeStorage.Returns(excludeStorage);
        config.PersistSnapshots.Returns(persistSnapshots);
        config.TrackDepthIncrementally.Returns(trackDepthIncrementally);
        return config;
    }

    /// <summary>
    /// Asserts equality of the six account-trie fields plus <see cref="CumulativeTrieStats.AccountsTotal"/>
    /// and <see cref="CumulativeTrieStats.ContractsTotal"/>. Intentionally does NOT cover the storage-trie
    /// side (<c>StorageSlotsTotal</c>, <c>StorageTrieBranches/Extensions/Leaves/Bytes</c>) —
    /// callers that exercise storage tries should assert those fields directly.
    /// </summary>
    public static void AssertAccountTrieFieldsEqual(
        CumulativeTrieStats actual, CumulativeTrieStats expected, string? context = null)
    {
        string prefix = context is null ? string.Empty : context + " — ";
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.AccountsTotal, Is.EqualTo(expected.AccountsTotal), prefix + "AccountsTotal");
            Assert.That(actual.ContractsTotal, Is.EqualTo(expected.ContractsTotal), prefix + "ContractsTotal");
            Assert.That(actual.AccountTrieBranches, Is.EqualTo(expected.AccountTrieBranches), prefix + "AccountTrieBranches");
            Assert.That(actual.AccountTrieExtensions, Is.EqualTo(expected.AccountTrieExtensions), prefix + "AccountTrieExtensions");
            Assert.That(actual.AccountTrieLeaves, Is.EqualTo(expected.AccountTrieLeaves), prefix + "AccountTrieLeaves");
            Assert.That(actual.AccountTrieBytes, Is.EqualTo(expected.AccountTrieBytes), prefix + "AccountTrieBytes");
        }
    }
}
