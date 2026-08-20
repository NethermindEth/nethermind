// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
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

    public static FrameSignatureForRpc[]? FromSignatures(TxFrameSignature[]? signatures) =>
        signatures?.Select(static s => new FrameSignatureForRpc(s)).ToArray();

    public static TxFrameSignature[]? ToSignatures(FrameSignatureForRpc[]? signatures) =>
        signatures?.Select(static s => s.ToSignature()).ToArray();
}
