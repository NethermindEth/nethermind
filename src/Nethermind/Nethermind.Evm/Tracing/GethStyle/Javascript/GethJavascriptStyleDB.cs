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
    private readonly V8ScriptEngine _engine;
    private readonly IWorldState _stateRepository;

    public GethJavascriptStyleDb(V8ScriptEngine engine, IWorldState stateRepository)
    {
        _engine = engine;
        _stateRepository = stateRepository;
    }

    public BigInteger getBalance(IList address) => (BigInteger)_stateRepository.GetBalance(address.ToAddress());

    public ulong getNonce(IList address) => (ulong)_stateRepository.GetNonce(address.ToAddress());

    public dynamic getCode(IList address) =>
        _stateRepository.GetCode(address.ToAddress()).ToScriptArray(_engine);

    public dynamic getState(IList address, IList index) =>
        _stateRepository.Get(new StorageCell(address.ToAddress(), index.GetUint256())).ToScriptArray(_engine);

    public bool exists(IList address) => !_stateRepository.GetAccount(address.ToAddress()).IsTotallyEmpty;
}
