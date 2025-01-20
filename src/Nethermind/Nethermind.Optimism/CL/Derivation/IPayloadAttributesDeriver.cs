// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Facade.Eth;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism.CL;

public interface IPayloadAttributesDeriver
{
    OptimismPayloadAttributes[] DerivePayloadAttributes(BatchV1 batch, BeaconBlock l1BeaconOrigin, BlockForRpc l1Origin, SystemConfig config);
}
