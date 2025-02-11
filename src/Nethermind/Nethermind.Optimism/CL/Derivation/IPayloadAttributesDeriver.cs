// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Data;
using Nethermind.Optimism.CL.Derivation;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism.CL;

public interface IPayloadAttributesDeriver
{
    // TODO: replace with l2block
    (OptimismPayloadAttributes[], SystemConfig[], L1BlockInfo[]) DerivePayloadAttributes(BatchV1 batch, L2Block l2Parent, BlockForRpc[] l1Origins, ReceiptForRpc[][] l1Receipts);
}
