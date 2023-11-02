// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public class GlobalFunctions
{
    private readonly IReleaseSpec _spec;

    public GlobalFunctions(V8ScriptEngine engine, IReleaseSpec spec)
    {
        _spec = spec;

        Func<object, object?> toWord = ToWord;
        Func<IList, string> toHex = ToHex;
        Func<object, object> toAddress = ToAddress;
        Func<object, bool> isPrecompiled = IsPrecompiled;
        Func<IList, long, long, ScriptObject> slice = Slice;
        Func<object, ulong, ScriptObject> toContract = ToContract;
        Func<object, string, object, ScriptObject> toContract2 = ToContract2;

        engine.AddHostObject(nameof(toWord), toWord);
        engine.AddHostObject(nameof(toHex), toHex);
        engine.AddHostObject(nameof(toAddress), toAddress);
        engine.AddHostObject(nameof(isPrecompiled), isPrecompiled);
        engine.AddHostObject(nameof(slice), slice);
        engine.AddHostObject(nameof(toContract), toContract);
        engine.AddHostObject(nameof(toContract2), toContract2);
    }

    private ScriptObject? ToWord(object bytes) => bytes.ToWord()?.ToScriptArray();
    private string ToHex(IList bytes) => bytes.ToHexString();
    private ScriptObject ToAddress(object address) => address.ToAddress().Bytes.ToScriptArray();
    private bool IsPrecompiled(object address) => address.ToAddress().IsPrecompile(_spec);
    private ScriptObject Slice(IList input, long start, long end) => input.Slice(start, end).ToScriptArray();
    private ScriptObject ToContract(object from, ulong nonce) => ContractAddress.From(from.ToAddress(), nonce).Bytes.ToScriptArray();
    private ScriptObject ToContract2(object from, string salt, object initcode) =>
        ContractAddress.From(from.ToAddress(), Bytes.FromHexString(salt), ValueKeccak.Compute(initcode.ToBytes()).Bytes).Bytes.ToScriptArray();
}
