// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Tracing;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using System;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Benchmarks.Core;
[MemoryDiagnoser]
public class CodeInfoRepositoryBenchmark
{

    private IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec(MainnetSpecProvider.PragueActivation);

    private AuthorizationTuple[] Tuples1;
    private AuthorizationTuple[] Tuples5;
    private AuthorizationTuple[] Tuples10;
    private AuthorizationTuple[] Tuples100;

    private CodeInfoRepository sut;
    private static EthereumEcdsa _ethereumEcdsa;
    private WorldState _stateProvider;
    private HashSet<Address> _accessedAddresses;

    [GlobalSetup]
    public void GlobalSetup()
    {
        TrieStore trieStore = new(new MemDb(), new OneLoggerLogManager(NullLogger.Instance));
        IKeyValueStore codeDb = new MemDb();

        _stateProvider = new WorldState(trieStore, codeDb, new OneLoggerLogManager(NullLogger.Instance));
        _stateProvider.CreateAccount(Address.Zero, 100000000000000);
        _stateProvider.Commit(_spec);

        _accessedAddresses = new HashSet<Address>();
        _ethereumEcdsa = new(1);
        sut = new();
        var list = new List<AuthorizationTuple>();
        var rnd = new Random();
        var addressBuffer = new byte[20];
        for (int i = 0; i < 1000; i++)
        {
            rnd.NextBytes(addressBuffer);
            PrivateKey signer = Build.A.PrivateKey.TestObject;
            list.Add(CreateAuthorizationTuple(
                signer,
                1,
                new Address(addressBuffer),
                1));
        }
        Tuples1 = list.Take(1).ToArray();
        Tuples5 = list.Skip(1).Take(5).ToArray();
        Tuples10 = list.Skip(6).Take(10).ToArray();
        Tuples100 = list.Skip(16).Take(100).ToArray();

        static AuthorizationTuple CreateAuthorizationTuple(PrivateKey signer, ulong chainId, Address codeAddress, ulong nonce)
        {
            AuthorizationTupleDecoder decoder = new();
            RlpStream rlp = decoder.EncodeWithoutSignature(chainId, codeAddress, nonce);
            Span<byte> code = stackalloc byte[rlp.Length + 1];
            code[0] = Eip7702Constants.Magic;
            rlp.Data.AsSpan().CopyTo(code.Slice(1));

            Signature sig = _ethereumEcdsa.Sign(signer, Keccak.Compute(code));

            return new AuthorizationTuple(chainId, codeAddress, nonce, sig, signer.Address);
        }
    }

    // [Benchmark]
    // public void Build1Tuples()
    // {
    //     sut.InsertFromAuthorizations(_stateProvider, Tuples1, _accessedAddresses, _spec);
    // }
    //
    // [Benchmark]
    // public void Build5Tuples()
    // {
    //     sut.InsertFromAuthorizations(_stateProvider, Tuples5, _accessedAddresses, _spec);
    // }
    //
    // [Benchmark]
    // public void Build10Tuples()
    // {
    //     sut.InsertFromAuthorizations(_stateProvider, Tuples10, _accessedAddresses, _spec);
    // }
    //
    // [Benchmark]
    // public void Build100Tuples()
    // {
    //     sut.InsertFromAuthorizations(_stateProvider, Tuples100, _accessedAddresses, _spec);
    // }
}
