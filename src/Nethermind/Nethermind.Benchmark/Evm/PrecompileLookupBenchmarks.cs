// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Evm;
using Nethermind.Specs.Forks;

namespace Nethermind.Benchmarks.Evm;

/// <summary>
/// Cost of resolving whether an address is a precompile, and of resolving the precompile behind it.
/// </summary>
/// <remarks>
/// Goes only through interfaces that both sides of the array-indexing change expose, so the same file
/// can be run against either commit. Addresses are pre-built and shuffled so the loop measures the
/// lookup rather than address construction or a perfectly predicted branch.
/// </remarks>
[MemoryDiagnoser]
public class PrecompileLookupBenchmarks
{
    private const int AddressCount = 1024;
    private const int Seed = 42;

    private readonly IReleaseSpec _spec = Osaka.Instance;

    private ICodeInfoRepository _codeInfoRepository = null!;
    private Address[] _contracts = null!;
    private Address[] _precompiles = null!;

    [GlobalSetup]
    public void Setup()
    {
        _codeInfoRepository = new CodeInfoRepository(TestWorldStateFactory.CreateForTest(), new EthereumPrecompileProvider());

        List<Address> precompiles = [];
        foreach (AddressAsKey precompile in _spec.Precompiles)
            precompiles.Add(precompile.Value);

        Random random = new(Seed);
        _precompiles = new Address[AddressCount];
        _contracts = new Address[AddressCount];
        byte[] bytes = new byte[Address.Size];
        for (int i = 0; i < AddressCount; i++)
        {
            _precompiles[i] = precompiles[random.Next(precompiles.Count)];
            random.NextBytes(bytes);
            _contracts[i] = new Address(bytes);
        }
    }

    /// <summary> The dominant case: a call target that is not a precompile. </summary>
    [Benchmark(OperationsPerInvoke = AddressCount)]
    public int IsPrecompile_Contract()
    {
        int found = 0;
        foreach (Address address in _contracts)
        {
            if (_spec.IsPrecompile(address)) found++;
        }

        return found;
    }

    [Benchmark(OperationsPerInvoke = AddressCount)]
    public int IsPrecompile_Precompile()
    {
        int found = 0;
        foreach (Address address in _precompiles)
        {
            if (_spec.IsPrecompile(address)) found++;
        }

        return found;
    }

    /// <summary> The membership check and the precompile lookup behind it, as a precompile CALL pays them. </summary>
    [Benchmark(OperationsPerInvoke = AddressCount)]
    public int GetCachedCodeInfo_Precompile()
    {
        int found = 0;
        foreach (Address address in _precompiles)
        {
            if (_codeInfoRepository.GetCachedCodeInfo(address, false, _spec, out _).IsPrecompile) found++;
        }

        return found;
    }
}
