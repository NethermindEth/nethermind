// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Legacy.Electra;

/// <summary>Electra <c>BeaconState</c>.</summary>
/// <remarks>
/// Each earlier fork's state derives from the next fork's and hides what that fork added, so a decoded state of any
/// fork is a <see cref="BeaconStateFulu"/> that the current accessors, caches and hasher can work on.
/// </remarks>
[SszContainer]
public partial class BeaconState : BeaconStateFulu
{
    [SszIgnore]
    public new ulong[]? ProposerLookahead { get => base.ProposerLookahead; set => base.ProposerLookahead = value; }
}
