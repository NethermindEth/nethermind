// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc.Data;
using Nethermind.Optimism.CL.Decoding;
using Nethermind.Optimism.CL.L1Bridge;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism.CL.Derivation;

public interface IPayloadAttributesDeriver
{
    PayloadAttributesRef? TryDerivePayloadAttributes(SingularBatch batch,
        PayloadAttributesRef parentPayloadAttributes, L1Block l1Origin, ReceiptForRpc[] l1Receipts);
}

public class PayloadAttributesRef
{
    public required ulong Number { get; init; }
    public required SystemConfig SystemConfig { get; init; }
    public required L1BlockInfo L1BlockInfo { get; init; }
    public required OptimismPayloadAttributes PayloadAttributes { get; init; }

    public override string ToString()
    {
        return $"Number: {Number}\n, SystemConfig: {SystemConfig}\n, L1BlockInfo: {L1BlockInfo}\n, PayloadAttributes: {PayloadAttributes}";
    }
}
