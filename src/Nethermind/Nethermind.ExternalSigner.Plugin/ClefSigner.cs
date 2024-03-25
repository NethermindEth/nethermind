// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.JsonRpc.Client;
using Nethermind.Serialization.Rlp;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nethermind.JsonRpc;
public class ClefSigner : ISigner, ISignerStore
{
    private readonly IJsonRpcClient rpcClient;
    private HeaderDecoder _headerDecoder;

    private ClefSigner(IJsonRpcClient rpcClient, Address author)
    {
        this.rpcClient = rpcClient;
        Address = author;
        _headerDecoder = new HeaderDecoder();
    }

    public static async Task<ClefSigner> Create(IJsonRpcClient jsonRpcClient, Address? blockAuthorAccount = null)
    {
        ClefSigner signer = new(jsonRpcClient, await GetSignerAddress(jsonRpcClient, blockAuthorAccount));
        return signer;
    }

    public Address Address { get; private set; }

    public bool CanSign => true;

    public bool CanSignHeader => true;

    public PrivateKey? Key => throw new InvalidOperationException("Cannot get private keys from remote signer.");

    /// <summary>
    /// Clef will not sign data directly, but will parse and sign data in the format: 
    /// keccak256("\x19Ethereum Signed Message:\n${message length}${message}")
    /// </summary>
    /// <param name="message">Message to be signed.</param>
    /// <returns><see cref="Signature"/> of <paramref name="message"/>.</returns>
    public Signature Sign(Hash256 message)
    {
        var signed = rpcClient.Post<string>(
            "account_signData",
            "text/plain",
            Address.ToString(),
            message).GetAwaiter().GetResult();
        if (signed == null)
            ThrowInvalidOperationSignFailed();
        var bytes = Bytes.FromHexString(signed);
        return new Signature(bytes);
    }

    /// <summary>
    /// Used to sign a clique header. The full Rlp of the header has to be sent,
    /// since clef does not sign data directly, but will parse and decide itself what to sign.
    /// </summary>
    /// <param name="rlpHeader">Full Rlp of the clique header.</param>
    /// <returns><see cref="Signature"/> of the hash of the clique header.</returns>
    public Signature Sign(BlockHeader header)
    {
        if (header is null) throw new ArgumentNullException(nameof(header));
        int contentLength = _headerDecoder.GetLength(header, RlpBehaviors.None);
        IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(contentLength);
        try
        {
            RlpStream rlpStream = new NettyRlpStream(buffer);
            rlpStream.Encode(header);
            string? signed = rpcClient.Post<string>(
                "account_signData",
                "application/x-clique-header",
                Address.ToString(),
                buffer.AsSpan().ToHexString(true)).GetAwaiter().GetResult();
            if (signed == null)
                ThrowInvalidOperationSignFailed();
            byte[] bytes = Bytes.FromHexString(signed);

            //Clef will set recid to 0/1, but we expect it to be 27/28
            if (bytes.Length == 65 && bytes[64] == 0 || bytes[64] == 1)
                //We expect V to be 27/28
                bytes[64] += 27;

            return new Signature(bytes);
        }
        finally
        {
            buffer.Release();
        }
    }

    public ValueTask Sign(Transaction tx) =>
        throw new NotImplementedException("Remote signing of transactions is not supported.");

    private async static Task<Address> GetSignerAddress(IJsonRpcClient rpcClient, Address? blockAuthorAccount)
    {
        var accounts = await rpcClient.Post<string[]>("account_list");
        if (accounts is null)
            throw new InvalidOperationException("Remote signer 'account_list' response is invalid.");
        if (!accounts.Any())
            throw new InvalidOperationException("Remote signer has not been configured with any signers.");
        if (blockAuthorAccount != null)
        {
            if (accounts.Any(a => new Address(a).Bytes.SequenceEqual(blockAuthorAccount.Bytes)))
                return blockAuthorAccount;
            else
                throw new InvalidOperationException($"Remote signer cannot sign for {blockAuthorAccount}.");
        }
        else
        {
            return new Address(accounts[0]);
        }
    }

    public void SetSigner(PrivateKey key)
    {
        ThrowInvalidOperationSetSigner();
    }

    public void SetSigner(ProtectedPrivateKey key)
    {
        ThrowInvalidOperationSetSigner();
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ThrowInvalidOperationSignFailed() =>
        throw new InvalidOperationException("Remote signer failed to sign the request.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ThrowInvalidOperationSetSigner() =>
        throw new InvalidOperationException("Cannot set a signer when using a remote signer.");
}


