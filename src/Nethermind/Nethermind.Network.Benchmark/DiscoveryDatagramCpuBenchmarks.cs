// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Net;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Network.Discovery.Discv4.Serializers;

namespace Nethermind.Network.Benchmarks;

[MemoryDiagnoser]
public class DiscoveryDatagramCpuBenchmarks
{
    [Params(false, true)]
    public bool InvalidMdc { get; set; }

    private byte[] _packet = null!;
    private MessageSerializationService _service = null!;

    [GlobalSetup]
    public void Setup()
    {
        Ecdsa ecdsa = new();
        PingMsgSerializer serializer = new(ecdsa, new SameKeyGenerator(TestItem.PrivateKeyA), new NodeIdResolver(ecdsa));
        _service = new MessageSerializationService(SerializerInfo.Create(serializer));
        IPEndPoint local = new(IPAddress.Parse("8.8.8.8"), 30303);
        IPEndPoint remote = new(IPAddress.Parse("9.9.9.9"), 30303);
        PingMsg message = new(TestItem.PublicKeyB, 2_000_000_000, local, remote, new byte[32]);
        using ArrayPoolSpan<byte> encoded = _service.Serialize(message);
        _packet = encoded.AsReadOnlyMemory().ToArray();
        if (InvalidMdc) _packet[0] ^= 1;
    }

    [Benchmark(Baseline = true)]
    public object? HashTwice()
    {
        if (!Accept()) return null;
        return _service.Deserialize<PingMsg>(_packet).FarPublicKey;
    }

    private bool Accept()
    {
        ValueHash256 mdc = ValueKeccak.Compute(_packet.AsSpan(Hash256.Size));
        return _packet.AsSpan(0, Hash256.Size).SequenceEqual(mdc.BytesAsSpan);
    }
}
