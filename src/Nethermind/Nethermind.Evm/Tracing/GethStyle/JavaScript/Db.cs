// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
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

namespace Nethermind.Evm.Tracing.GethStyle.JavaScript;

public class Db
{
    public IWorldState WorldState { get; }

    public Db(IWorldState worldState) => WorldState = worldState;

    public IJavaScriptObject getBalance(object address) => WorldState.GetBalance(address.ToAddress()).ToBigInteger();

    public ulong getNonce(object address) => (ulong)WorldState.GetNonce(address.ToAddress());

    public ITypedArray<byte> getCode(object address) => WorldState.GetCode(address.ToAddress()).ToTypedScriptArray();

    public ITypedArray<byte> getState(object address, object hash)
    {
        byte[] array = ArrayPool<byte>.Shared.Rent(32);
        try
        {
            ReadOnlySpan<byte> bytes = WorldState.Get(new StorageCell(address.ToAddress(), hash.GetHash()));
            if (bytes.Length < array.Length)
            {
                Array.Clear(array);
            }
            bytes.CopyTo(array.AsSpan(array.Length - bytes.Length));
            return array.ToTypedScriptArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(array);
        }
    }

    public bool exists(object address) => !WorldState.GetAccount(address.ToAddress()).IsTotallyEmpty;
}
