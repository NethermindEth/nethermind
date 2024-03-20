// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Linq;
using Microsoft.ClearScript.JavaScript;
using Nethermind.Core;
using Nethermind.Core.Extensions;
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
        byte[] array = WorldState.Get(new StorageCell(address.ToAddress(), hash.GetHash()))
            .ToArray(includeLeadingZeroes: true);
        return array.ToTypedScriptArray();
    }

    public bool exists(object address) => WorldState.TryGetAccount(address.ToAddress(), out AccountStruct account) && !account.IsTotallyEmpty;
}
