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
    private readonly IWorldState _stateRepository;

    public GethJavascriptStyleDb(IWorldState stateRepository)
    {
        _stateRepository = stateRepository;
    }

    public BigInteger getBalance(IList address) => (BigInteger)_stateRepository.GetBalance(address.ToAddress());

    public ulong getNonce(IList address) => (ulong)_stateRepository.GetNonce(address.ToAddress());

    public dynamic getCode(IList address) =>
        _stateRepository.GetCode(address.ToAddress()).ToScriptArray(Engine);

    public dynamic getState(IList address, IList index) =>
        _stateRepository.Get(new StorageCell(address.ToAddress(), index.GetUint256())).ToScriptArray(Engine);

    public bool exists(IList address) => !_stateRepository.GetAccount(address.ToAddress()).IsTotallyEmpty;
}
