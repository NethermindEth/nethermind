// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
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
    private static readonly Address PrecompileAddress = Address.FromNumber(100);

    private static PreBlockCaches CreateCaches(int survivingMaxEntries = 1024) =>
        new(new PreBlockCachesConfig { SurvivingPrecompileCacheMaxEntries = survivingMaxEntries });

    private static IReleaseSpec CreateSpecWithPrecompiles(params Address[] precompileAddresses)
    {
        IReleaseSpec spec = ReleaseSpecSubstitute.Create();
        spec.Precompiles.Returns(precompileAddresses.Select(a => (AddressAsKey)a).ToFrozenSet());
        return spec;
    }

    private static PrecompileCachedCodeInfoRepository BuildRepository(PreBlockCaches? caches, params (Address Address, IPrecompile Precompile)[] precompiles)
    {
        FrozenDictionary<AddressAsKey, CodeInfo> map = precompiles
            .ToDictionary(p => (AddressAsKey)p.Address, p => new CodeInfo(p.Precompile))
            .ToFrozenDictionary();
        IPrecompileProvider provider = Substitute.For<IPrecompileProvider>();
        provider.GetPrecompiles().Returns(map);
        return new PrecompileCachedCodeInfoRepository(Substitute.For<IWorldState>(), provider, Substitute.For<ICodeInfoRepository>(), caches);
    }

    private static IPrecompile ResolvePrecompile(PreBlockCaches? caches, IPrecompile precompile, Address? address = null)
    {
        address ??= PrecompileAddress;
        PrecompileCachedCodeInfoRepository repository = BuildRepository(caches, (address, precompile));
        return repository.GetCachedCodeInfo(address, false, CreateSpecWithPrecompiles(address), out _).Precompile!;
    }

    [Test]
    public void IsCodeOverridable_WhenBaseIsOverridable_PropagatesIt()
    {
        IPrecompileProvider provider = Substitute.For<IPrecompileProvider>();
        provider.GetPrecompiles().Returns(FrozenDictionary<AddressAsKey, CodeInfo>.Empty);
        ICodeInfoRepository baseRepository = Substitute.For<ICodeInfoRepository>();
        baseRepository.IsCodeOverridable.Returns(true);

        PrecompileCachedCodeInfoRepository repository = new(Substitute.For<IWorldState>(), provider, baseRepository, null);

        Assert.That(repository.IsCodeOverridable, Is.True, "the wrapper must not hide the base repository's capability");
    }

    [TestCase(true, true, true, TestName = "GetCachedCodeInfo_CachingPrecompileWithCaches_IsWrapped")]
    [TestCase(false, true, false, TestName = "GetCachedCodeInfo_NonCachingPrecompileWithCaches_IsNotWrapped")]
    [TestCase(true, false, false, TestName = "GetCachedCodeInfo_CachingPrecompileWithoutCaches_IsNotWrapped")]
    public void GetCachedCodeInfo_AtGivenCachingSupport_WrapsOnlyCacheablePrecompiles(bool supportsCaching, bool withCaches, bool expectWrapped)
    {
        TestPrecompile precompile = new(supportsCaching);

        IPrecompile resolved = ResolvePrecompile(withCaches ? CreateCaches() : null, precompile);

        if (expectWrapped)
        {
            Assert.That(resolved, Is.Not.SameAs(precompile), "a cacheable precompile must be wrapped when caches are supplied");
            Assert.That(resolved.GetType().Name, Does.Contain("CachedPrecompile"));
        }
        else
        {
            Assert.That(resolved, Is.SameAs(precompile), "the original precompile must be served unwrapped");
        }
    }

    [Test]
    public void GetCachedCodeInfo_WithMixedRealPrecompiles_WrapsOnlyCachingOnes()
    {
        PrecompileCachedCodeInfoRepository repository = BuildRepository(CreateCaches(),
            (Sha256Precompile.Address, Sha256Precompile.Instance),
            (IdentityPrecompile.Address, IdentityPrecompile.Instance));
        IReleaseSpec spec = CreateSpecWithPrecompiles(Sha256Precompile.Address, IdentityPrecompile.Address);

        IPrecompile sha256 = repository.GetCachedCodeInfo(Sha256Precompile.Address, false, spec, out _).Precompile!;
        IPrecompile identity = repository.GetCachedCodeInfo(IdentityPrecompile.Address, false, spec, out _).Precompile!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sha256, Is.Not.SameAs(Sha256Precompile.Instance), "sha256 supports caching and must be wrapped");
            Assert.That(identity, Is.SameAs(IdentityPrecompile.Instance), "identity does not support caching and must stay unwrapped");
        }
    }

    [TestCase(true, 1, 1, TestName = "Run_ForRepeatedInputWhenCaching_ComputesOnce")]
    [TestCase(false, 2, 0, TestName = "Run_ForRepeatedInputWhenNotCaching_ComputesEveryTime")]
    public void Run_AtGivenCachingSupport_ComputesOncePerUniqueInput(bool supportsCaching, int expectedRuns, int expectedEntries)
    {
        int runCount = 0;
        byte[] fixedOutput = [10, 20, 30];
        TestPrecompile precompile = new(supportsCaching, onRun: () => runCount++, fixedOutput: fixedOutput);
        PreBlockCaches caches = CreateCaches();
        IPrecompile resolved = ResolvePrecompile(caches, precompile);

        byte[] input = [1, 2, 3];
        Result<byte[]> first = resolved.Run(input, Prague.Instance);
        Result<byte[]> second = resolved.Run(input, Prague.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runCount, Is.EqualTo(expectedRuns), "a cacheable repeated input must compute exactly once");
            Assert.That(caches.PrecompileCache.Count, Is.EqualTo(expectedEntries), "only cacheable results may enter the per-block tier");
            Assert.That(first.Data, Is.EqualTo(fixedOutput), "the computed result must be served");
            Assert.That(second.Data, Is.EqualTo(fixedOutput), "a hit must be indistinguishable from a computation");
        }
    }

    [Test]
    public void Run_ForDifferentInputs_CreatesSeparateEntries()
    {
        int runCount = 0;
        PreBlockCaches caches = CreateCaches();
        IPrecompile resolved = ResolvePrecompile(caches, new TestPrecompile(supportsCaching: true, onRun: () => runCount++));

        resolved.Run(new byte[] { 1, 2, 3 }, Prague.Instance);
        resolved.Run(new byte[] { 4, 5, 6 }, Prague.Instance);
        resolved.Run(new byte[] { 1, 2, 3 }, Prague.Instance);
        resolved.Run(new byte[] { 4, 5, 6 }, Prague.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runCount, Is.EqualTo(2), "each unique input must compute exactly once");
            Assert.That(caches.PrecompileCache.Count, Is.EqualTo(2), "each unique input must have its own entry");
        }
    }

    [Test]
    public void Run_WithRealSha256_ServesTheComputedDigestFromTheCache()
    {
        PreBlockCaches caches = CreateCaches();
        IPrecompile resolved = ResolvePrecompile(caches, Sha256Precompile.Instance, Sha256Precompile.Address);

        byte[] input = [1, 2, 3, 4, 5];
        Result<byte[]> first = resolved.Run(input, Prague.Instance);
        Result<byte[]> second = resolved.Run(input, Prague.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((bool)first, Is.True, "precondition: the real digest computation succeeds");
            Assert.That(second.Data, Is.EqualTo(first.Data), "the cached digest must match the computed one");
            Assert.That(caches.PrecompileCache.Count, Is.EqualTo(1), "the digest must be cached");
        }
    }

    [Test]
    public void Run_WithNormalizedOversizedInputs_DeduplicatesToOneEntry()
    {
        int runCount = 0;
        PreBlockCaches caches = CreateCaches();
        IPrecompile resolved = ResolvePrecompile(caches, new TruncatingTestPrecompile(effectiveLength: 4, onRun: () => runCount++));

        Result<byte[]> first = resolved.Run(new byte[] { 1, 2, 3, 4, 0xAA, 0xBB }, Prague.Instance);
        Result<byte[]> second = resolved.Run(new byte[] { 1, 2, 3, 4, 0xCC, 0xDD, 0xEE }, Prague.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runCount, Is.EqualTo(1), "inputs equal after normalization must map to the same cache key");
            Assert.That(caches.PrecompileCache.Count, Is.EqualTo(1));
            Assert.That(second.Data, Is.EqualTo(first.Data));
        }
    }

    [Test]
    public void Run_ForInvalidLengthResults_DoesNotCache()
    {
        int runCount = 0;
        PreBlockCaches caches = CreateCaches();
        IPrecompile resolved = ResolvePrecompile(caches, new FixedLengthTestPrecompile(validLength: 4, onRun: () => runCount++));

        Result<byte[]> first = resolved.Run(new byte[] { 1, 2, 3 }, Prague.Instance);
        Result<byte[]> repeat = resolved.Run(new byte[] { 1, 2, 3 }, Prague.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((bool)first, Is.False, "precondition: an invalid-length input must fail");
            Assert.That((bool)repeat, Is.False);
            Assert.That(runCount, Is.EqualTo(2), "invalid-length results must re-run instead of being cached");
            Assert.That(caches.PrecompileCache.Count, Is.EqualTo(0), "the block tier must remain empty for invalid-length results");
        }
    }

    [Test]
    public void Run_ForDifferentSpecs_CreatesSeparateCacheEntries()
    {
        int runCount = 0;
        PreBlockCaches caches = CreateCaches();
        IPrecompile resolved = ResolvePrecompile(caches, new TestPrecompile(supportsCaching: true, onRun: () => runCount++));

        byte[] input = [1, 2, 3];
        resolved.Run(input, Prague.Instance);
        resolved.Run(input, Osaka.Instance);
        resolved.Run(input, Prague.Instance);
        resolved.Run(input, Osaka.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runCount, Is.EqualTo(2), "an entry cached under one spec must not be served under another");
            Assert.That(caches.PrecompileCache.Count, Is.EqualTo(2), "each spec must have its own entry for the same input");
        }
    }

    [Test]
    public void Run_AfterPerBlockClear_ServesFromTheSurvivingTier()
    {
        int runCount = 0;
        PreBlockCaches caches = CreateCaches();
        IPrecompile resolved = ResolvePrecompile(caches, new TestPrecompile(supportsCaching: true, onRun: () => runCount++));

        byte[] input = [1, 2, 3];
        resolved.Run(input, Prague.Instance);
        caches.ClearCaches();
        Assert.That(caches.PrecompileCache.Count, Is.EqualTo(0), "precondition: the per-block tier is cleared");

        resolved.Run(input, Prague.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runCount, Is.EqualTo(1), "the surviving tier must serve the result across the per-block clear");
            Assert.That(caches.PrecompileCache.Count, Is.EqualTo(1), "a surviving-tier hit must backfill the per-block tier");
        }
    }

    [Test]
    public void Run_AtSurvivingTierCapacity_EvictsInsteadOfGrowing()
    {
        int runCount = 0;
        PreBlockCaches caches = CreateCaches(survivingMaxEntries: 2);
        IPrecompile resolved = ResolvePrecompile(caches, new TestPrecompile(supportsCaching: true, onRun: () => runCount++));

        resolved.Run(new byte[] { 1 }, Prague.Instance);
        resolved.Run(new byte[] { 2 }, Prague.Instance);
        resolved.Run(new byte[] { 3 }, Prague.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runCount, Is.EqualTo(3), "precondition: three distinct inputs must each compute");
            Assert.That(caches.SurvivingPrecompileCache.Count, Is.EqualTo(2), "the surviving tier must evict at capacity instead of growing");
        }
    }

    [Test]
    public void Run_WithOversizedEntry_DoesNotSurviveTheBlock()
    {
        int runCount = 0;
        PreBlockCaches caches = CreateCaches();
        IPrecompile resolved = ResolvePrecompile(caches, new TestPrecompile(supportsCaching: true, onRun: () => runCount++));

        byte[] oversizedInput = new byte[4096];
        resolved.Run(oversizedInput, Prague.Instance);
        resolved.Run(oversizedInput, Prague.Instance);
        Assert.That(runCount, Is.EqualTo(1), "precondition: within the block the oversized entry is served by the per-block tier");

        caches.ClearCaches();
        resolved.Run(oversizedInput, Prague.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runCount, Is.EqualTo(2), "oversized entries must recompute after the per-block clear");
            Assert.That(caches.SurvivingPrecompileCache.Count, Is.EqualTo(0), "entries above the byte cap must not enter the surviving tier");
        }
    }

    private class TestPrecompile(bool supportsCaching, Action? onRun = null, byte[]? fixedOutput = null) : IPrecompile
    {
        public bool SupportsCaching => supportsCaching;

        public ulong BaseGasCost(IReleaseSpec releaseSpec) => 0UL;

        public ulong DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0UL;

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

        public ulong BaseGasCost(IReleaseSpec releaseSpec) => 0UL;

        public ulong DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0UL;

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

        public ulong BaseGasCost(IReleaseSpec releaseSpec) => 0UL;

        public ulong DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0UL;

        public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            onRun?.Invoke();
            if (inputData.Length != validLength)
                return Errors.InvalidInputLength;
            return inputData.ToArray();
        }
    }
}
