// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Data;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc;
public class RemoteSigner : ISigner
{
    private readonly IJsonRpcClient rpcClient;
    private readonly ulong _chainId;

    private RemoteSigner(IJsonRpcClient rpcClient, ulong chainId)
    {
        this.rpcClient = rpcClient;
        this._chainId = chainId;
        CanSign = true;
    }

    public static async Task<RemoteSigner> Create(IJsonRpcClient jsonRpcClient, ulong chainId, Address? blockAuthorAccount = null)
    {
        RemoteSigner signer =new (jsonRpcClient, chainId);
        await signer.SetSignerAddress(blockAuthorAccount);
        return signer;
    }

    public Address Address { get; private set; }

    public bool CanSign { get; }

    public PrivateKey? Key => throw new InvalidOperationException("Private keys cannot be exposed.");

    public Signature Sign(Hash256 message)
    {
        //TODO handle async
        var signed = rpcClient.Post<string>("account_signData", "application/clique", Address.ToString(), message).GetAwaiter().GetResult();
        if (signed == null)
            ThrowInvalidOperation();
        return new Signature(Bytes.FromHexString(signed));
    }

    public async ValueTask Sign(Transaction tx)
    {
        TransactionForRpc transactionModel = new(tx);
        var signed = await rpcClient.Post<TransactionForRpc>("account_signTransaction", transactionModel);
        if (signed == null)
            ThrowInvalidOperation();
        tx.Signature = new Signature(signed.R.Value!, signed.S.Value!, (ulong)signed.V);
    }

    private async Task SetSignerAddress(Address? blockAuthorAccount)
    {
        var accounts = await rpcClient.Post<string[]>("account_list");
        if (!accounts.Any())
        {
            throw new InvalidOperationException("Remote signer has not been configured with any signers.");
        }
        if (blockAuthorAccount != null)
        {
            if (accounts.Any(a=>new Address(a).Bytes.SequenceEqual(blockAuthorAccount.Bytes)))
                Address = blockAuthorAccount;
            else
                throw new InvalidOperationException($"Remote signer cannot sign for the specified block author {blockAuthorAccount}.");
        }
        else
        {
            Address = new Address(accounts[0]);
        }
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ThrowInvalidOperation() =>
        throw new InvalidOperationException("Remote signer failed to respond appropriately to signature request.");

}
