// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class PrecompileCachedCodeInfoRepositoryTests
{
    private static IReleaseSpec CreateSpecWithPrecompile(Address precompileAddress)
    {
        IReleaseSpec spec = ReleaseSpecSubstitute.Create();
        spec.Precompiles.Returns(new HashSet<AddressAsKey> { precompileAddress }.ToFrozenSet());
        return spec;
    }

    [Test]
    public void Precompile_WithCachingEnabled_IsWrappedInCachedPrecompile()
    {
        // Arrange
        TestPrecompile cachingPrecompile = new(supportsCaching: true);
        Address precompileAddress = Address.FromNumber(100);

        FrozenDictionary<AddressAsKey, CodeInfo> precompiles = new Dictionary<AddressAsKey, CodeInfo>
        {
            [precompileAddress] = new(cachingPrecompile)
        }.ToFrozenDictionary();

        IPrecompileProvider precompileProvider = Substitute.For<IPrecompileProvider>();
        precompileProvider.GetPrecompiles().Returns(precompiles);

        ICodeInfoRepository baseRepository = Substitute.For<ICodeInfoRepository>();
        ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, Result<byte[]>> cache = new();

        IReleaseSpec spec = CreateSpecWithPrecompile(precompileAddress);

        // Act
        PrecompileCachedCodeInfoRepository repository = new(precompileProvider, baseRepository, cache);
        CodeInfo codeInfo = repository.GetCachedCodeInfo(precompileAddress, false, spec, out _);

        // Assert
        codeInfo.Should().NotBeNull();
        codeInfo.Precompile.Should().NotBeSameAs(cachingPrecompile);
        codeInfo.Precompile!.GetType().Name.Should().Contain("CachedPrecompile");
    }

    [Test]
    public void Precompile_WithCachingDisabled_IsNotWrapped()
    {
        // Arrange
        TestPrecompile nonCachingPrecompile = new(supportsCaching: false);
        Address precompileAddress = Address.FromNumber(100);

        FrozenDictionary<AddressAsKey, CodeInfo> precompiles = new Dictionary<AddressAsKey, CodeInfo>
        {
            [precompileAddress] = new(nonCachingPrecompile)
        }.ToFrozenDictionary();

        IPrecompileProvider precompileProvider = Substitute.For<IPrecompileProvider>();
        precompileProvider.GetPrecompiles().Returns(precompiles);

        ICodeInfoRepository baseRepository = Substitute.For<ICodeInfoRepository>();
        ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, Result<byte[]>> cache = new();

        IReleaseSpec spec = CreateSpecWithPrecompile(precompileAddress);

        // Act
        PrecompileCachedCodeInfoRepository repository = new(precompileProvider, baseRepository, cache);
        CodeInfo codeInfo = repository.GetCachedCodeInfo(precompileAddress, false, spec, out _);

        // Assert
        codeInfo.Should().NotBeNull();
        codeInfo.Precompile.Should().BeSameAs(nonCachingPrecompile);
    }

    [Test]
    public void IdentityPrecompile_IsNotWrapped_WhenCacheEnabled()
    {
        // Arrange
        FrozenDictionary<AddressAsKey, CodeInfo> precompiles = new Dictionary<AddressAsKey, CodeInfo>
        {
            [IdentityPrecompile.Address] = new(IdentityPrecompile.Instance)
        }.ToFrozenDictionary();

        IPrecompileProvider precompileProvider = Substitute.For<IPrecompileProvider>();
        precompileProvider.GetPrecompiles().Returns(precompiles);

        ICodeInfoRepository baseRepository = Substitute.For<ICodeInfoRepository>();
        ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, Result<byte[]>> cache = new();

        IReleaseSpec spec = CreateSpecWithPrecompile(IdentityPrecompile.Address);

        // Act
        PrecompileCachedCodeInfoRepository repository = new(precompileProvider, baseRepository, cache);
        CodeInfo codeInfo = repository.GetCachedCodeInfo(IdentityPrecompile.Address, false, spec, out _);

        // Assert
        codeInfo.Should().NotBeNull();
        codeInfo.Precompile.Should().BeSameAs(IdentityPrecompile.Instance);
    }

    [Test]
    public void CachedPrecompile_CachesResults_ForCachingEnabledPrecompile()
    {
        // Arrange
        int runCount = 0;
        TestPrecompile cachingPrecompile = new(supportsCaching: true, onRun: () => runCount++);
        Address precompileAddress = Address.FromNumber(100);

        FrozenDictionary<AddressAsKey, CodeInfo> precompiles = new Dictionary<AddressAsKey, CodeInfo>
        {
            [precompileAddress] = new(cachingPrecompile)
        }.ToFrozenDictionary();

        IPrecompileProvider precompileProvider = Substitute.For<IPrecompileProvider>();
        precompileProvider.GetPrecompiles().Returns(precompiles);

        ICodeInfoRepository baseRepository = Substitute.For<ICodeInfoRepository>();
        ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, Result<byte[]>> cache = new();

        IReleaseSpec spec = CreateSpecWithPrecompile(precompileAddress);

        PrecompileCachedCodeInfoRepository repository = new(precompileProvider, baseRepository, cache);
        CodeInfo codeInfo = repository.GetCachedCodeInfo(precompileAddress, false, spec, out _);

        byte[] input = [1, 2, 3];

        // Act - run twice with same input
        codeInfo.Precompile!.Run(input, Prague.Instance);
        codeInfo.Precompile!.Run(input, Prague.Instance);

        // Assert - should only run once due to caching
        runCount.Should().Be(1);
        cache.Count.Should().Be(1);
    }

    [Test]
    public void NonCachingPrecompile_DoesNotCacheResults()
    {
        // Arrange
        int runCount = 0;
        TestPrecompile nonCachingPrecompile = new(supportsCaching: false, onRun: () => runCount++);
        Address precompileAddress = Address.FromNumber(100);

        FrozenDictionary<AddressAsKey, CodeInfo> precompiles = new Dictionary<AddressAsKey, CodeInfo>
        {
            [precompileAddress] = new(nonCachingPrecompile)
        }.ToFrozenDictionary();

        IPrecompileProvider precompileProvider = Substitute.For<IPrecompileProvider>();
        precompileProvider.GetPrecompiles().Returns(precompiles);

        ICodeInfoRepository baseRepository = Substitute.For<ICodeInfoRepository>();
        ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, Result<byte[]>> cache = new();

        IReleaseSpec spec = CreateSpecWithPrecompile(precompileAddress);

        PrecompileCachedCodeInfoRepository repository = new(precompileProvider, baseRepository, cache);
        CodeInfo codeInfo = repository.GetCachedCodeInfo(precompileAddress, false, spec, out _);

        byte[] input = [1, 2, 3];

        // Act - run twice with same input
        codeInfo.Precompile!.Run(input, Prague.Instance);
        codeInfo.Precompile!.Run(input, Prague.Instance);

        // Assert - should run twice since caching is disabled
        runCount.Should().Be(2);
        cache.Count.Should().Be(0);
    }

    [Test]
    public void NullCache_DoesNotWrapAnyPrecompiles()
    {
        // Arrange
        TestPrecompile cachingPrecompile = new(supportsCaching: true);
        Address precompileAddress = Address.FromNumber(100);

        FrozenDictionary<AddressAsKey, CodeInfo> precompiles = new Dictionary<AddressAsKey, CodeInfo>
        {
            [precompileAddress] = new(cachingPrecompile)
        }.ToFrozenDictionary();

        IPrecompileProvider precompileProvider = Substitute.For<IPrecompileProvider>();
        precompileProvider.GetPrecompiles().Returns(precompiles);

        ICodeInfoRepository baseRepository = Substitute.For<ICodeInfoRepository>();

        IReleaseSpec spec = CreateSpecWithPrecompile(precompileAddress);

        // Act - pass null cache
        PrecompileCachedCodeInfoRepository repository = new(precompileProvider, baseRepository, null);
        CodeInfo codeInfo = repository.GetCachedCodeInfo(precompileAddress, false, spec, out _);

        // Assert - precompile should not be wrapped
        codeInfo.Should().NotBeNull();
        codeInfo.Precompile.Should().BeSameAs(cachingPrecompile);
    }

    [Test]
    public void Sha256Precompile_IsWrapped_WhenCacheEnabled()
    {
        // Arrange - Sha256Precompile has SupportsCaching = true (default)
        FrozenDictionary<AddressAsKey, CodeInfo> precompiles = new Dictionary<AddressAsKey, CodeInfo>
        {
            [Sha256Precompile.Address] = new(Sha256Precompile.Instance)
        }.ToFrozenDictionary();

        IPrecompileProvider precompileProvider = Substitute.For<IPrecompileProvider>();
        precompileProvider.GetPrecompiles().Returns(precompiles);

        ICodeInfoRepository baseRepository = Substitute.For<ICodeInfoRepository>();
        ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, Result<byte[]>> cache = new();

        IReleaseSpec spec = CreateSpecWithPrecompile(Sha256Precompile.Address);

        // Act
        PrecompileCachedCodeInfoRepository repository = new(precompileProvider, baseRepository, cache);
        CodeInfo codeInfo = repository.GetCachedCodeInfo(Sha256Precompile.Address, false, spec, out _);

        // Assert - Sha256Precompile should be wrapped (unlike IdentityPrecompile)
        codeInfo.Should().NotBeNull();
        codeInfo.Precompile.Should().NotBeSameAs(Sha256Precompile.Instance);
        codeInfo.Precompile!.GetType().Name.Should().Contain("CachedPrecompile");
    }

    [Test]
    public void MixedPrecompiles_OnlyCachingEnabledAreWrapped()
    {
        // Arrange - mix of caching and non-caching precompiles
        FrozenDictionary<AddressAsKey, CodeInfo> precompiles = new Dictionary<AddressAsKey, CodeInfo>
        {
            [Sha256Precompile.Address] = new(Sha256Precompile.Instance),      // SupportsCaching = true
            [IdentityPrecompile.Address] = new(IdentityPrecompile.Instance)   // SupportsCaching = false
        }.ToFrozenDictionary();

        IPrecompileProvider precompileProvider = Substitute.For<IPrecompileProvider>();
        precompileProvider.GetPrecompiles().Returns(precompiles);

        ICodeInfoRepository baseRepository = Substitute.For<ICodeInfoRepository>();
        ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, Result<byte[]>> cache = new();

        IReleaseSpec spec = ReleaseSpecSubstitute.Create();
        spec.Precompiles.Returns(new HashSet<AddressAsKey>
        {
            Sha256Precompile.Address,
            IdentityPrecompile.Address
        }.ToFrozenSet());

        // Act
        PrecompileCachedCodeInfoRepository repository = new(precompileProvider, baseRepository, cache);
        CodeInfo sha256CodeInfo = repository.GetCachedCodeInfo(Sha256Precompile.Address, false, spec, out _);
        CodeInfo identityCodeInfo = repository.GetCachedCodeInfo(IdentityPrecompile.Address, false, spec, out _);

        // Assert - Sha256 wrapped, Identity not wrapped
        sha256CodeInfo.Precompile.Should().NotBeSameAs(Sha256Precompile.Instance);
        sha256CodeInfo.Precompile!.GetType().Name.Should().Contain("CachedPrecompile");

        identityCodeInfo.Precompile.Should().BeSameAs(IdentityPrecompile.Instance);
    }

    [Test]
    public void CachedPrecompile_DifferentInputs_CreateSeparateCacheEntries()
    {
        // Arrange
        int runCount = 0;
        TestPrecompile cachingPrecompile = new(supportsCaching: true, onRun: () => runCount++);
        Address precompileAddress = Address.FromNumber(100);

        FrozenDictionary<AddressAsKey, CodeInfo> precompiles = new Dictionary<AddressAsKey, CodeInfo>
        {
            [precompileAddress] = new(cachingPrecompile)
        }.ToFrozenDictionary();

        IPrecompileProvider precompileProvider = Substitute.For<IPrecompileProvider>();
        precompileProvider.GetPrecompiles().Returns(precompiles);

        ICodeInfoRepository baseRepository = Substitute.For<ICodeInfoRepository>();
        ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, Result<byte[]>> cache = new();

        IReleaseSpec spec = CreateSpecWithPrecompile(precompileAddress);

        PrecompileCachedCodeInfoRepository repository = new(precompileProvider, baseRepository, cache);
        CodeInfo codeInfo = repository.GetCachedCodeInfo(precompileAddress, false, spec, out _);

        byte[] input1 = [1, 2, 3];
        byte[] input2 = [4, 5, 6];

        // Act - run with different inputs
        codeInfo.Precompile!.Run(input1, Prague.Instance);
        codeInfo.Precompile!.Run(input2, Prague.Instance);
        codeInfo.Precompile!.Run(input1, Prague.Instance); // should hit cache
        codeInfo.Precompile!.Run(input2, Prague.Instance); // should hit cache

        // Assert - should run twice (once per unique input), cache should have 2 entries
        runCount.Should().Be(2);
        cache.Count.Should().Be(2);
    }

    [Test]
    public void CachedPrecompile_ReturnsCachedResult_OnCacheHit()
    {
        // Arrange
        int runCount = 0;
        byte[] expectedOutput = [10, 20, 30];
        TestPrecompile cachingPrecompile = new(supportsCaching: true, onRun: () => runCount++, fixedOutput: expectedOutput);
        Address precompileAddress = Address.FromNumber(100);

        FrozenDictionary<AddressAsKey, CodeInfo> precompiles = new Dictionary<AddressAsKey, CodeInfo>
        {
            [precompileAddress] = new(cachingPrecompile)
        }.ToFrozenDictionary();

        IPrecompileProvider precompileProvider = Substitute.For<IPrecompileProvider>();
        precompileProvider.GetPrecompiles().Returns(precompiles);

        ICodeInfoRepository baseRepository = Substitute.For<ICodeInfoRepository>();
        ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, Result<byte[]>> cache = new();

        IReleaseSpec spec = CreateSpecWithPrecompile(precompileAddress);

        PrecompileCachedCodeInfoRepository repository = new(precompileProvider, baseRepository, cache);
        CodeInfo codeInfo = repository.GetCachedCodeInfo(precompileAddress, false, spec, out _);

        byte[] input = [1, 2, 3];

        // Act - run twice with same input
        Result<byte[]> result1 = codeInfo.Precompile!.Run(input, Prague.Instance);
        Result<byte[]> result2 = codeInfo.Precompile!.Run(input, Prague.Instance);

        // Assert - both results should be the same cached value
        runCount.Should().Be(1);
        ((bool)result1).Should().BeTrue();
        ((bool)result2).Should().BeTrue();
        result1.Data.Should().BeEquivalentTo(expectedOutput);
        result2.Data.Should().BeEquivalentTo(expectedOutput);
    }

    [Test]
    public void Sha256Precompile_CachesResults_WithRealComputation()
    {
        // Arrange
        FrozenDictionary<AddressAsKey, CodeInfo> precompiles = new Dictionary<AddressAsKey, CodeInfo>
        {
            [Sha256Precompile.Address] = new(Sha256Precompile.Instance)
        }.ToFrozenDictionary();

        IPrecompileProvider precompileProvider = Substitute.For<IPrecompileProvider>();
        precompileProvider.GetPrecompiles().Returns(precompiles);

        ICodeInfoRepository baseRepository = Substitute.For<ICodeInfoRepository>();
        ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, Result<byte[]>> cache = new();

        IReleaseSpec spec = CreateSpecWithPrecompile(Sha256Precompile.Address);

        PrecompileCachedCodeInfoRepository repository = new(precompileProvider, baseRepository, cache);
        CodeInfo codeInfo = repository.GetCachedCodeInfo(Sha256Precompile.Address, false, spec, out _);

        byte[] input = [1, 2, 3, 4, 5];

        // Act - run twice with same input
        Result<byte[]> result1 = codeInfo.Precompile!.Run(input, Prague.Instance);
        Result<byte[]> result2 = codeInfo.Precompile!.Run(input, Prague.Instance);

        // Assert - results should match and cache should have entry
        ((bool)result1).Should().BeTrue();
        ((bool)result2).Should().BeTrue();
        result1.Data.Should().BeEquivalentTo(result2.Data);
        cache.Count.Should().Be(1);
    }

    [Test]
    public void IdentityPrecompile_DoesNotCache_WithRealComputation()
    {
        // Arrange
        FrozenDictionary<AddressAsKey, CodeInfo> precompiles = new Dictionary<AddressAsKey, CodeInfo>
        {
            [IdentityPrecompile.Address] = new(IdentityPrecompile.Instance)
        }.ToFrozenDictionary();

        IPrecompileProvider precompileProvider = Substitute.For<IPrecompileProvider>();
        precompileProvider.GetPrecompiles().Returns(precompiles);

        ICodeInfoRepository baseRepository = Substitute.For<ICodeInfoRepository>();
        ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, Result<byte[]>> cache = new();

        IReleaseSpec spec = CreateSpecWithPrecompile(IdentityPrecompile.Address);

        PrecompileCachedCodeInfoRepository repository = new(precompileProvider, baseRepository, cache);
        CodeInfo codeInfo = repository.GetCachedCodeInfo(IdentityPrecompile.Address, false, spec, out _);

        byte[] input = [1, 2, 3, 4, 5];

        // Act - run twice with same input
        Result<byte[]> result1 = codeInfo.Precompile!.Run(input, Prague.Instance);
        Result<byte[]> result2 = codeInfo.Precompile!.Run(input, Prague.Instance);

        // Assert - results should match but cache should be empty (no caching for Identity)
        ((bool)result1).Should().BeTrue();
        ((bool)result2).Should().BeTrue();
        result1.Data.Should().BeEquivalentTo(result2.Data);
        cache.Count.Should().Be(0); // Key difference from Sha256 test
    }

    [Test]
    public void CachedPrecompile_WithNormalizeInputOverride_DeduplicatesOversizedInputs()
    {
        // Precompile that only uses the first 4 bytes of input.
        int runCount = 0;
        TruncatingTestPrecompile precompile = new(effectiveLength: 4, onRun: () => runCount++);
        Address precompileAddress = Address.FromNumber(100);

        FrozenDictionary<AddressAsKey, CodeInfo> precompiles = new Dictionary<AddressAsKey, CodeInfo>
        {
            [precompileAddress] = new(precompile)
        }.ToFrozenDictionary();

        IPrecompileProvider precompileProvider = Substitute.For<IPrecompileProvider>();
        precompileProvider.GetPrecompiles().Returns(precompiles);

        ICodeInfoRepository baseRepository = Substitute.For<ICodeInfoRepository>();
        ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, Result<byte[]>> cache = new();

        IReleaseSpec spec = CreateSpecWithPrecompile(precompileAddress);

        PrecompileCachedCodeInfoRepository repository = new(precompileProvider, baseRepository, cache);
        CodeInfo codeInfo = repository.GetCachedCodeInfo(precompileAddress, false, spec, out _);

        // Same first 4 bytes, different suffixes — both calls should map to the same cache key.
        byte[] input1 = [1, 2, 3, 4, 0xAA, 0xBB];
        byte[] input2 = [1, 2, 3, 4, 0xCC, 0xDD, 0xEE];

        Result<byte[]> result1 = codeInfo.Precompile!.Run(input1, Prague.Instance);
        Result<byte[]> result2 = codeInfo.Precompile!.Run(input2, Prague.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runCount, Is.EqualTo(1), "precompile should run only once; second call must hit cache");
            Assert.That(cache.Count, Is.EqualTo(1));
            Assert.That(result1.Data, Is.EqualTo(result2.Data));
        }
    }

    [Test]
    public void CachedPrecompile_DoesNotCache_InvalidLengthResults()
    {
        int runCount = 0;
        FixedLengthTestPrecompile precompile = new(validLength: 4, onRun: () => runCount++);
        Address precompileAddress = Address.FromNumber(100);

        FrozenDictionary<AddressAsKey, CodeInfo> precompiles = new Dictionary<AddressAsKey, CodeInfo>
        {
            [precompileAddress] = new(precompile)
        }.ToFrozenDictionary();

        IPrecompileProvider precompileProvider = Substitute.For<IPrecompileProvider>();
        precompileProvider.GetPrecompiles().Returns(precompiles);

        ICodeInfoRepository baseRepository = Substitute.For<ICodeInfoRepository>();
        ConcurrentDictionary<PreBlockCaches.PrecompileCacheKey, Result<byte[]>> cache = new();

        IReleaseSpec spec = CreateSpecWithPrecompile(precompileAddress);

        PrecompileCachedCodeInfoRepository repository = new(precompileProvider, baseRepository, cache);
        CodeInfo codeInfo = repository.GetCachedCodeInfo(precompileAddress, false, spec, out _);

        byte[] input1 = [1, 2, 3];          // length 3, not 4
        byte[] input2 = [1, 2, 3, 4, 5];    // length 5, not 4
        byte[] input3 = [1, 2, 3, 4, 5, 6]; // length 6, not 4

        Result<byte[]> result1 = codeInfo.Precompile!.Run(input1, Prague.Instance);
        Result<byte[]> result2 = codeInfo.Precompile.Run(input2, Prague.Instance);
        Result<byte[]> result3 = codeInfo.Precompile.Run(input3, Prague.Instance);
        Result<byte[]> result1Again = codeInfo.Precompile.Run(input1, Prague.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((bool)result1, Is.False, "invalid-length input must fail");
            Assert.That((bool)result2, Is.False);
            Assert.That((bool)result3, Is.False);
            Assert.That((bool)result1Again, Is.False);
            Assert.That(runCount, Is.EqualTo(4), "each call must re-run; invalid-length results must not be cached");
            Assert.That(cache, Is.Empty, "cache must remain empty for invalid-length results");
        }
    }

    private class TestPrecompile(bool supportsCaching, Action? onRun = null, byte[]? fixedOutput = null) : IPrecompile
    {
        public bool SupportsCaching => supportsCaching;

        public long BaseGasCost(IReleaseSpec releaseSpec) => 0;

        public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0;

        public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            onRun?.Invoke();
            return fixedOutput ?? inputData.ToArray();
        }
    }

    private class TruncatingTestPrecompile(int effectiveLength, Action? onRun = null) : IPrecompile
    {
        public bool SupportsCaching => true;

        public ReadOnlyMemory<byte> NormalizeInput(ReadOnlyMemory<byte> inputData) =>
            inputData.Length > effectiveLength ? inputData[..effectiveLength] : inputData;

        public long BaseGasCost(IReleaseSpec releaseSpec) => 0;

        public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0;

        public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            onRun?.Invoke();
            return inputData.Length > effectiveLength
                ? inputData[..effectiveLength].ToArray()
                : inputData.ToArray();
        }
    }

    private class FixedLengthTestPrecompile(int validLength, Action? onRun = null) : IPrecompile
    {
        public bool SupportsCaching => true;

        public long BaseGasCost(IReleaseSpec releaseSpec) => 0;

        public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0;

        public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            onRun?.Invoke();
            if (inputData.Length != validLength)
                return Errors.InvalidInputLength;
            return inputData.ToArray();
        }
    }
}
