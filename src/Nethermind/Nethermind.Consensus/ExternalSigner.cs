// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.JsonRpc.Client;
using Org.BouncyCastle.Utilities.Encoders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Consensus;
public class ExternalSigner : ISigner
{
    private readonly IJsonRpcClient rpcClient;

    private ExternalSigner(IJsonRpcClient rpcClient)
    {
        this.rpcClient = rpcClient;
        CanSign = true;
    }

    public static async Task<ExternalSigner> Create(IJsonRpcClient jsonRpcClient)
    {
        ExternalSigner signer =new (jsonRpcClient);
        await signer.SetSignerAddress();
        return signer;
    }

    public Address Address { get; private set; }

    public bool CanSign { get; }

    public PrivateKey? Key => throw new InvalidOperationException("Private keys cannot be exposed.");

    public Signature Sign(Hash256 message)
    {
        //TODO handle async
        var signed = rpcClient.Post<string>("account_signData", "application/clique", Address.ToString(), message).GetAwaiter().GetResult();
        return new Signature(Bytes.FromHexString(signed));
    }

    public ValueTask Sign(Transaction tx)
    {
        throw new NotImplementedException();
    }

    private async Task SetSignerAddress()
    {
        //TODO sort the list and validate
        var accounts = await rpcClient.Post<string[]>("account_list");
        Address = new Address(accounts[0]);
    }
}
