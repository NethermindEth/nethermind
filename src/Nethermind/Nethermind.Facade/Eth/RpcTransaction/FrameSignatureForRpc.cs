// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
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

    public static FrameSignatureForRpc[]? FromSignatures(TxFrameSignature[]? signatures) =>
        signatures?.Select(static s => new FrameSignatureForRpc(s)).ToArray();

    /// <summary>Maps the deserialized <c>signatures</c> list onto the transaction's frame signatures.</summary>
    /// <param name="signatures">The deserialized list, or <c>null</c> when the request omitted it.</param>
    /// <param name="converted">The mapped list, or <c>null</c> when <paramref name="signatures"/> is absent.</param>
    /// <returns><c>false</c> if any element was JSON <c>null</c>.</returns>
    public static bool TryToSignatures(FrameSignatureForRpc[]? signatures, out TxFrameSignature[]? converted) =>
        RpcListConverter.TryConvert(signatures, static s => s.ToSignature(), out converted);
}
