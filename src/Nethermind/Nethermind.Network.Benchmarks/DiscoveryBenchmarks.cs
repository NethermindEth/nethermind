//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Net;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.Serializers;

namespace Nethermind.Network.Benchmarks
{
    [MemoryDiagnoser]
    [CoreJob(true)]
    public class DiscoveryBenchmarks
    {
        [GlobalSetup]
        public void GlobalSetup()
        {
            _pingMessage = new PingMessage();
            _pingMessage.SourceAddress = new IPEndPoint(IPAddress.Loopback, IPEndPoint.MaxPort);
            _pingMessage.DestinationAddress = new IPEndPoint(IPAddress.Broadcast, IPEndPoint.MaxPort);
            _pingMessage.ExpirationTime = 123456789;
        }

        private NewPingMessageSerializer _pingSerializer = new NewPingMessageSerializer(new Ecdsa(), new PrivateKeyGenerator(new CryptoRandom()), new DiscoveryMessageFactory(Timestamper.Default), new NodeIdResolver(new Ecdsa()));
        private PingMessageSerializer _newPingSerializer = new PingMessageSerializer(new Ecdsa(), new PrivateKeyGenerator(new CryptoRandom()), new DiscoveryMessageFactory(Timestamper.Default), new NodeIdResolver(new Ecdsa()));
        private PingMessage _pingMessage;

        [Benchmark(Baseline = true)]
        public byte[] Old()
        {
            return _pingSerializer.Serialize(_pingMessage);
        }

        [Benchmark]
        public byte[] New()
        {
            return _newPingSerializer.Serialize(_pingMessage);
        }
    }
}