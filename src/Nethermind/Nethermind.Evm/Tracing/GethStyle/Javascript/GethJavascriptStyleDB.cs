// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Linq;
using System.Numerics;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public class GethJavascriptStyleDb
{
    public V8ScriptEngine Engine { get; set; } = null!;
    public IWorldState WorldState { get; }

    public GethJavascriptStyleDb(IWorldState worldState)
    {
        WorldState = worldState;
    }

    public BigInteger getBalance(IList address) => (BigInteger)WorldState.GetBalance(address.ToAddress());

    public ulong getNonce(IList address) => (ulong)WorldState.GetNonce(address.ToAddress());

    public dynamic getCode(IList address) =>
        WorldState.GetCode(address.ToAddress()).ToScriptArray(Engine);

    public dynamic getState(IList address, IList index) =>
        WorldState.Get(new StorageCell(address.ToAddress(), index.GetUint256())).ToScriptArray(Engine);

    public bool exists(IList address) => !WorldState.GetAccount(address.ToAddress()).IsTotallyEmpty;
}
