// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Linq;
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

    public UInt256 getBalance(IList address) => _stateRepository.GetBalance(address.GetAddress());

    public UInt256 getNonce(IList address) => _stateRepository.GetNonce(address.GetAddress());

    public dynamic getCode(IList address) =>
        _stateRepository.GetCode(address.GetAddress()).ToScriptArray(_engine);

    public dynamic getState(IList address, IList index) =>
        _stateRepository.Get(new StorageCell(address.GetAddress(), index.GetUint256())).ToScriptArray(_engine);

    public bool exists(IList address) => !_stateRepository.GetAccount(address.GetAddress()).IsTotallyEmpty;
}
