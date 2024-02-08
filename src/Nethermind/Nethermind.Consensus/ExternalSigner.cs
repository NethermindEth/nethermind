// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Consensus;
public class ExternalSigner : ISigner
{
    private readonly string jsonRpcUrl;

    //public ExternalSigner(IJsonRpcClient rpcClient)
    //{
    //    this.jsonRpcUrl = jsonRpcUrl;

    //}
    public PrivateKey? Key => throw new NotImplementedException();

    public Address Address => throw new NotImplementedException();

    public bool CanSign => throw new NotImplementedException();

    public Signature Sign(Hash256 message)
    {
        throw new NotImplementedException();
    }

    public ValueTask Sign(Transaction tx)
    {
        throw new NotImplementedException();
    }
}
