// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using BenchmarkDotNet.Attributes;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Network.Benchmarks;

/// <summary>
/// Measures serializing an RLPx handshake message into its plaintext and deserializing it back, the way the handshake service does.
/// </summary>
/// <remarks>
/// EIP-8 padding is left out of serialization, because it is random and not part of the message encoding.
/// </remarks>
[MemoryDiagnoser]
public class HandshakeMessageBenchmarks
{
    public enum Message
    {
        Auth,
        AuthEip8,
        Ack,
        AckEip8
    }

    // The middle of the 100-300 bytes of EIP-8 padding that follows the RLP body of an auth plaintext
    private const int AuthEip8Padding = 200;

    private IContainer _container = null!;
    private Func<int> _serialize = null!;
    private Func<byte[], MessageBase> _deserialize = null!;
    private byte[] _plainText = null!;

    [Params(Message.Auth, Message.AuthEip8, Message.Ack, Message.AckEip8)]
    public Message Type { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        PrivateKey key = TestItem.PrivateKeyA;
        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters(),
            Allocations = [],
            Genesis = Build.A.Block.TestObject
        };
        _container = new ContainerBuilder()
            .AddModule(new PseudoNethermindModule(chainSpec, new ConfigProvider(), LimboLogs.Instance))
            .AddModule(new TestEnvironmentModule(key, null))
            .AddSingleton<IMessagePad, NoPad>()
            .Build();
        IMessageSerializationService service = _container.Resolve<IMessageSerializationService>();

        Signature signature = new Ecdsa().Sign(key, Keccak.Zero);
        byte[] nonce = TestItem.KeccakB.BytesToArray();
        switch (Type)
        {
            case Message.Auth:
                SetUp(service, new AuthMessage
                {
                    Signature = signature,
                    EphemeralPublicHash = TestItem.KeccakA,
                    PublicKey = key.PublicKey,
                    Nonce = nonce
                });
                break;
            case Message.AuthEip8:
                SetUp(service, new AuthEip8Message { Signature = signature, PublicKey = key.PublicKey, Nonce = nonce });
                _plainText = [.. _plainText, .. new byte[AuthEip8Padding]];
                break;
            case Message.Ack:
                SetUp(service, new AckMessage { EphemeralPublicKey = TestItem.PublicKeyB, Nonce = nonce });
                break;
            case Message.AckEip8:
                SetUp(service, new AckEip8Message { EphemeralPublicKey = TestItem.PublicKeyB, Nonce = nonce });
                break;
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _container.Dispose();

    [Benchmark]
    public int Serialize() => _serialize();

    [Benchmark]
    public MessageBase Deserialize() => _deserialize(_plainText);

    private void SetUp<T>(IMessageSerializationService service, T message) where T : MessageBase
    {
        _serialize = () =>
        {
            IByteBuffer plainText = service.ZeroSerialize(message);
            try
            {
                return plainText.ReadableBytes;
            }
            finally
            {
                plainText.SafeRelease();
            }
        };
        _deserialize = plainText => service.Deserialize<T>(plainText);

        IByteBuffer plainText = service.ZeroSerialize(message);
        try
        {
            _plainText = plainText.ReadAllBytesAsArray();
        }
        finally
        {
            plainText.SafeRelease();
        }
    }
}
