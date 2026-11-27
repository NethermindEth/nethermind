// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Legacy.Deneb;

/// <summary>Deneb <c>BeaconBlockBody</c>: the Electra body with committee-scoped attestations and no execution requests.</summary>
/// <remarks>
/// Each earlier fork's containers derive from the next fork's and hide what that fork added, so that one working
/// state and the current block processing helpers serve every fork. A hidden or retyped reference property forwards
/// to its base; the attestation lists cannot, because their element types differ.
/// </remarks>
[SszContainer]
public partial class BeaconBlockBody : Nethermind.BeaconChain.Types.BeaconBlockBody
{
    [SszList(LegacyPresets.MaxAttesterSlashings)]
    public new AttesterSlashing[]? AttesterSlashings { get; set; }
    [SszList(LegacyPresets.MaxAttestations)]
    public new Attestation[]? Attestations { get; set; }
    [SszIgnore]
    public new ExecutionRequests? ExecutionRequests { get => base.ExecutionRequests; set => base.ExecutionRequests = value; }
}

/// <summary>Deneb <c>BeaconBlock</c>.</summary>
[SszContainer]
public partial class BeaconBlock : Nethermind.BeaconChain.Types.BeaconBlock
{
    public new BeaconBlockBody? Body
    {
        get => (BeaconBlockBody?)base.Body;
        set => base.Body = value;
    }
}

/// <summary>Deneb <c>SignedBeaconBlock</c>.</summary>
[SszContainer]
public partial class SignedBeaconBlock : Nethermind.BeaconChain.Types.SignedBeaconBlock
{
    public new BeaconBlock? Message
    {
        get => (BeaconBlock?)base.Message;
        set => base.Message = value;
    }
}
