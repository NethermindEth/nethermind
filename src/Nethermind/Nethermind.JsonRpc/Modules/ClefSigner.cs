// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing.GethStyle.JavaScript;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Data;
using Nethermind.Serialization.Rlp;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc;
public class ClefSigner : ISigner, ISignerStore
{
    private readonly IJsonRpcClient rpcClient;
    private readonly ulong _chainId;

    private ClefSigner(IJsonRpcClient rpcClient, ulong chainId)
    {
        this.rpcClient = rpcClient;
        this._chainId = chainId;
        CanSign = true;
    }

    public static async Task<ClefSigner> Create(IJsonRpcClient jsonRpcClient, ulong chainId, Address? blockAuthorAccount = null)
    {
        ClefSigner signer = new(jsonRpcClient, chainId);
        await signer.SetSignerAddress(blockAuthorAccount);
        return signer;
    }

    public Address Address { get; private set; }

    public bool CanSign { get; }

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
    public Signature SignCliqueHeader(byte[] rlpHeader)
    {
        var signed = rpcClient.Post<string>(
            "account_signData",
            "application/x-clique-header",
            Address.ToString(),
            rlpHeader.ToHexString(true)).GetAwaiter().GetResult();
        if (signed == null)
            ThrowInvalidOperationSignFailed();
        var bytes = Bytes.FromHexString(signed);

        //Clef will set recid to 0/1, but we expect it to be 27/28
        if (bytes.Length == 65 && bytes[64] == 0 || bytes[64] == 1)
            //We expect V to be 27/28
            bytes[64] += 27;

        return new Signature(bytes);
    }

    public ValueTask Sign(Transaction tx) =>
        throw new NotImplementedException("Remote signing of transactions is not supported.");

    private async Task SetSignerAddress(Address? blockAuthorAccount)
    {
        var accounts = await rpcClient.Post<string[]>("account_list");
        if (!accounts.Any())
        {
            throw new InvalidOperationException("Remote signer has not been configured with any signers.");
        }
        if (blockAuthorAccount != null)
        {
            if (accounts.Any(a => new Address(a).Bytes.SequenceEqual(blockAuthorAccount.Bytes)))
                Address = blockAuthorAccount;
            else
                throw new InvalidOperationException($"Remote signer cannot sign for {blockAuthorAccount}.");
        }
        else
        {
            Address = new Address(accounts[0]);
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


    private class RemoteTxSignResponse
    {
        public string Raw { get; set; }
        public TransactionForRpc Tx { get; set; }
    }
}


