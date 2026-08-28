// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;

namespace Nethermind.Facade.Eth.RpcTransaction;

/// <summary>JSON-RPC view of an EIP-8141 signature entry: <c>[scheme, signer, msg, signature]</c>.</summary>
/// <remarks>Raw signature bytes are surfaced here deliberately: the EIP-8141 introspection limits bind the EVM, not RPC.</remarks>
public class FrameSignatureForRpc
{
    public byte Scheme { get; set; }
    public Address? Signer { get; set; }
    public byte[] Msg { get; set; } = [];
    public byte[] Signature { get; set; } = [];

    [JsonConstructor]
    public FrameSignatureForRpc() { }

    public FrameSignatureForRpc(TxFrameSignature signature)
    {
        Scheme = signature.Scheme;
        Signer = signature.Signer;
        Msg = signature.Msg.ToArray();
        Signature = signature.Signature.ToArray();
    }

    public TxFrameSignature ToSignature() => new(Scheme, Signer, Msg, Signature);

    public static FrameSignatureForRpc[]? FromSignatures(TxFrameSignature[]? signatures)
    {
        if (signatures is null) return null;

        FrameSignatureForRpc[] result = new FrameSignatureForRpc[signatures.Length];
        for (int i = 0; i < signatures.Length; i++)
        {
            result[i] = new FrameSignatureForRpc(signatures[i]);
        }

        return result;
    }

    /// <summary>Maps the deserialized <c>signatures</c> list onto the transaction's frame signatures.</summary>
    /// <param name="signatures">The deserialized list, or <c>null</c> when the request omitted it.</param>
    /// <param name="converted">The mapped list, or <c>null</c> when <paramref name="signatures"/> is absent.</param>
    /// <returns><c>false</c> if any element was JSON <c>null</c>.</returns>
    public static bool TryToSignatures(FrameSignatureForRpc[]? signatures, out TxFrameSignature[]? converted) =>
        RpcListConverter.TryConvert(signatures, static s => s.ToSignature(), out converted);
}
