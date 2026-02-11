// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;

namespace Nethermind.Benchmarks.Evm
{
    [MemoryDiagnoser]
    public class EcRecoverBenchmark
    {
        private readonly EthereumEcdsa _ethereumEcdsa = new(1);
        private Signature _signature;
        private ValueHash256 _messageHash;
        private Address _expectedAddress;

        [GlobalSetup]
        public void Setup()
        {
            _messageHash = ValueKeccak.Compute(Bytes.FromHexString("0x0102030405060708090a0b0c0d0e0f10"));
            _signature = _ethereumEcdsa.Sign(TestItem.PrivateKeyA, in _messageHash);
            _expectedAddress = TestItem.PrivateKeyA.Address;

            if (!Current() || !Improved())
            {
                throw new InvalidBenchmarkDeclarationException("ecRecover mismatch");
            }
        }

        [Benchmark(Baseline = true)]
        public bool Improved()
        {
            return _ethereumEcdsa.RecoverAddress(_signature, in _messageHash) == _expectedAddress;
        }

        [Benchmark]
        public bool Current()
        {
            PublicKey recovered = _ethereumEcdsa.RecoverPublicKey(_signature, in _messageHash);
            return recovered is not null && recovered.Address == _expectedAddress;
        }
    }
}
