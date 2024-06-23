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
using Nethermind.Serialization.Rlp.Eip7702;
using Nethermind.Serialization.Rlp;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Benchmarks.Core;
[MemoryDiagnoser]
public class AuthorizedCodeInfoRepositoryBenchmark
{

    private IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec(MainnetSpecProvider.PragueActivation);

    private AuthorizationTuple[] Tuples100;
    private AuthorizationTuple[] Tuples1k;
    private AuthorizationTuple[] Tuples10k;

    private AuthorizedCodeInfoRepository sut;
    private static EthereumEcdsa _ethereumEcdsa;
    private WorldState _stateProvider;

    [GlobalSetup]
    public void GlobalSetup()
    {
        TrieStore trieStore = new(new MemDb(), new OneLoggerLogManager(NullLogger.Instance));
        IKeyValueStore codeDb = new MemDb();

        _stateProvider = new WorldState(trieStore, codeDb, new OneLoggerLogManager(NullLogger.Instance));
        _stateProvider.CreateAccount(Address.Zero, 100000000000000);
        _stateProvider.Commit(_spec);

        _ethereumEcdsa = new (1, new OneLoggerLogManager(NullLogger.Instance));
        sut = new AuthorizedCodeInfoRepository(1, NullLogger.Instance);
        var list = new List<AuthorizationTuple>();
        var rnd = new Random();
        var addressBuffer = new byte[20];
        for (int i = 0; i < 10000; i++)
        {
            rnd.NextBytes(addressBuffer);
            list.Add(CreateAuthorizationTuple(
                TestItem.PrivateKeys[rnd.Next(TestItem.PrivateKeys.Length - 1)],
                1,
                new Address(addressBuffer),
                1));
        }
        Tuples100 = list.Take(100).ToArray();
        Tuples1k = list.Take(1_000).ToArray();
        Tuples10k = list.Take(10_000).ToArray();

        static AuthorizationTuple CreateAuthorizationTuple(PrivateKey signer, ulong chainId, Address codeAddress, UInt256? nonce)
        {
            AuthorizationListDecoder decoder = new();
            RlpStream rlp = decoder.EncodeForCommitMessage(chainId, codeAddress, nonce);
            Span<byte> code = stackalloc byte[rlp.Length + 1];
            code[0] = Eip7702Constants.Magic;
            rlp.Data.AsSpan().CopyTo(code.Slice(1));

            Signature sig = _ethereumEcdsa.Sign(signer, Keccak.Compute(code));

            return new AuthorizationTuple(chainId, codeAddress, nonce, sig);
        }
    }

    [Benchmark]
    public void Build100Tuples()
    {
        sut.BuildAuthorizedCodeFromAuthorizations(_stateProvider, Tuples100, _spec);
    }
    
    [Benchmark]
    public void Build1kTuples()
    {
        sut.BuildAuthorizedCodeFromAuthorizations(_stateProvider, Tuples1k, _spec);
    }

    //[Benchmark]
    //public void Build10kTuples()
    //{
    //    sut.BuildAuthorizedCodeFromAuthorizations(_stateProvider, Tuples10k, _spec);
    //}

}
