// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;

namespace Nethermind.Facade.Eth.RpcTransaction;

/// <summary>
/// JSON-RPC view of an EIP-8141 signature entry: <c>[scheme, signer, msg, signature]</c>. The raw
/// signature bytes of protocol-validated schemes are still surfaced here for observability; EVM
/// introspection restrictions apply only inside the VM, not to the RPC representation.
/// </summary>
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

    public static TxFrameSignature[]? ToSignatures(FrameSignatureForRpc[]? signatures)
    {
        if (signatures is null) return null;

        TxFrameSignature[] result = new TxFrameSignature[signatures.Length];
        for (int i = 0; i < signatures.Length; i++)
        {
            result[i] = signatures[i].ToSignature();
        }

        return result;
    }
}
