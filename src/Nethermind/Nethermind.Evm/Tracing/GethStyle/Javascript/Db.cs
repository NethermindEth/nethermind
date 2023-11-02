// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Linq;
using System.Numerics;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public class Db
{
    public IWorldState WorldState { get; }

    public Db(IWorldState worldState) => WorldState = worldState;

    public BigInteger getBalance(object address) => (BigInteger)WorldState.GetBalance(address.ToAddress());

    public ulong getNonce(object address) => (ulong)WorldState.GetNonce(address.ToAddress());

    public ITypedArray<byte> getCode(object address) => WorldState.GetCode(address.ToAddress()).ToScriptArray();

    public ITypedArray<byte> getState(object address, object index) =>
        WorldState.Get(new StorageCell(address.ToAddress(), index.GetUint256())).ToScriptArray();

    public bool exists(IList address) => !WorldState.GetAccount(address.ToAddress()).IsTotallyEmpty;
}
