// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Runner.Ethereum.WebTransport;

internal sealed class BrowserProtocol(
    IEthereumEcdsa ecdsa,
    [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey,
    WebTxHandler transactions,
    WebDiscHandler discovery) : IDisposable
{
    internal const int NonceLength = 32;
    private readonly PrivateKey _privateKey = nodeKey.Unprotect();

    public void Dispose() => _privateKey.Dispose();

    internal async Task ExchangeAsync(Stream stream, BrowserCapability capability, byte[] nonce, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(CreateHandshake(capability, nonce), cancellationToken);
        int maxRequestSize = capability == BrowserCapability.WebTx ? WebTxHandler.MaxRequestSize : WebDiscHandler.MaxRequestSize;
        (IMemoryOwner<byte> owner, int length) = await BrowserFrames.ReadRequestAsync(stream, maxRequestSize, cancellationToken);
        byte[] result;
        using (owner)
        {
            ReadOnlyMemory<byte> payload = owner.Memory[..length];
            result = capability == BrowserCapability.WebTx
                ? transactions.Handle(payload.Span, cancellationToken)
                : await discovery.HandleAsync(payload, cancellationToken);
        }

        await BrowserFrames.WriteResponseAsync(stream, result, cancellationToken);
    }

    internal byte[] CreateHandshake(BrowserCapability capability, ReadOnlySpan<byte> nonce)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(nonce.Length, NonceLength);
        ReadOnlySpan<byte> protocol = capability switch
        {
            BrowserCapability.WebTx => "webtx/1"u8,
            BrowserCapability.WebDisc => "webdisc/1"u8,
            _ => throw new ArgumentOutOfRangeException(nameof(capability))
        };

        // Geth uses the label and raw nonce as the signature preimage, independent of the CONNECT header encoding.
        ReadOnlySpan<byte> label = "ENR-key-proof-v1"u8;
        Span<byte> challenge = stackalloc byte[label.Length + NonceLength];
        label.CopyTo(challenge);
        nonce.CopyTo(challenge[label.Length..]);
        ValueHash256 digest = ValueKeccak.Compute(challenge);
        using Signature signature = ecdsa.Sign(_privateKey, in digest);
        Span<byte> proof = stackalloc byte[Signature.Size];
        signature.WriteBytesWithRecoveryTo(proof);

        int capabilityLength = Rlp.LengthOf(protocol);
        int contentLength = Rlp.LengthOf(proof) + Rlp.LengthOfSequence(capabilityLength);
        byte[] handshake = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = RlpWriter.ContentWriter(handshake);
        RlpSequence outer = writer.StartSequence(contentLength);
        writer.Write(proof);
        RlpSequence capabilities = writer.StartSequence(capabilityLength);
        writer.Write(protocol);
        writer.EndSequence(capabilities);
        writer.EndSequence(outer);
        return handshake;
    }
}
