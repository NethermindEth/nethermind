// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Coordinates single-execution witness capture for the primary block-processing path.
/// </summary>
public interface IWitnessCaptureRegistry
{
    Task<Witness?> ArmCapture(Hash256 blockHash);

    bool HasPendingCapture(Hash256 blockHash);

    bool TryDrainCapture(Hash256 blockHash, BlockHeader parentHeader, WitnessCapturingWorldStateProxy proxy);

    void DisarmCapture(Hash256 blockHash);
}
