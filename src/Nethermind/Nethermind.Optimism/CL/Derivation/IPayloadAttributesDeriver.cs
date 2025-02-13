// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Data;
using Nethermind.Optimism.CL.Decoding;
using Nethermind.Optimism.CL.Derivation;
using Nethermind.Optimism.CL.L1Bridge;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism.CL;

public interface IPayloadAttributesDeriver
{
    PayloadAttributesRef[] DerivePayloadAttributes(BatchV1 batch, L2Block l2Parent, L1Block[] l1Origins, ReceiptForRpc[][] l1Receipts);
}

public struct PayloadAttributesRef
{
    public required ulong Number;
    public required SystemConfig SystemConfig;
    public required L1BlockInfo L1BlockInfo;
    public required OptimismPayloadAttributes PayloadAttributes;
}
