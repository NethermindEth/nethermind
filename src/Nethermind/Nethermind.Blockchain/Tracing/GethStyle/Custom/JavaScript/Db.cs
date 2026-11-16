// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.ClearScript.JavaScript;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;

public class Db(IWorldState worldState)
{
    public IJavaScriptObject getBalance(object address) => worldState.GetBalance(address.ToAddress()).ToBigInteger();

    public ulong getNonce(object address) => worldState.GetNonce(address.ToAddress());

    public ITypedArray<byte> getCode(object address) => worldState.GetCode(address.ToAddress()).ToTypedScriptArray();

    public ITypedArray<byte> getState(object address, object index)
    {
        using ArrayPoolDisposableReturn handle = ArrayPoolDisposableReturn.Rent(32, out byte[] array);

        worldState.Get(new StorageCell(address.ToAddress(), new UInt256(index.ToBytes(), isBigEndian: true)), out UInt256 value);
        value.ToBigEndian(array);
        return array.ToTypedScriptArray();
    }

    public bool exists(object address) => worldState.TryGetAccount(address.ToAddress(), out AccountStruct account) && !account.IsTotallyEmpty;
}
