// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Linq;
using System.Numerics;
using Microsoft.ClearScript.JavaScript;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing.GethStyle.JavaScript;

public static class JavaScriptConverter
{
    public static byte[] ToBytes(this object input) => input switch
    {
        string hexString => Bytes.FromHexString(hexString),
        ITypedArray<byte> typedArray => typedArray.Length == 0 ? Array.Empty<byte>() : typedArray.ToArray(),
        IArrayBuffer arrayBuffer => arrayBuffer.GetBytes(),
        IArrayBufferView arrayBufferView => arrayBufferView.GetBytes(),
        IList list => list.ToEnumerable().Select(Convert.ToByte).ToArray(),
        _ => throw new ArgumentException(nameof(input))
    };

    public static byte[] ToWord(this object input) => input switch
    {
        string hexString => Bytes.FromHexString(hexString, EvmPooledMemory.WordSize),
        _ => ListToWord(input)
    };

    private static byte[] ListToWord(object input) => input.ToBytes().PadLeft(EvmPooledMemory.WordSize);

    public static Address ToAddress(this object address) => address switch
    {
        string hexString => Address.TryParseVariableLength(hexString, out Address parsedAddress, true)
            ? parsedAddress
            : throw new ArgumentException("Not correct address", nameof(address)),
        _ => new Address(address.ToBytes())
    } ?? throw new ArgumentException("Not correct address", nameof(address));

    public static ValueHash256 GetHash(this object index) => new(index.ToBytes());

    private static Engine CurrentEngine => Engine.CurrentEngine ?? throw new InvalidOperationException("No engine set");

    public static ITypedArray<byte> ToTypedScriptArray(this byte[] array) => CurrentEngine.CreateUint8Array(array);

    public static ITypedArray<byte> ToTypedScriptArray(this ReadOnlyMemory<byte> memory) => memory.ToArray().ToTypedScriptArray();

    public static IJavaScriptObject ToBigInteger(this BigInteger bigInteger) => CurrentEngine.CreateBigInteger(bigInteger);
    public static IJavaScriptObject ToBigInteger(this UInt256 bigInteger) => CurrentEngine.CreateBigInteger((BigInteger)bigInteger);



}
