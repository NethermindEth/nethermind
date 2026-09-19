// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Types;

/// <summary>Phase0 <c>Fork</c>.</summary>
[SszContainer]
public partial class Fork
{
    [SszVector(4)]
    public byte[]? PreviousVersion { get; set; }

    [SszVector(4)]
    public byte[]? CurrentVersion { get; set; }

    public ulong Epoch { get; set; }
}

/// <summary>Phase0 <c>ForkData</c>.</summary>
[SszContainer]
public partial class ForkData
{
    [SszVector(4)]
    public byte[]? CurrentVersion { get; set; }

    public Hash256? GenesisValidatorsRoot { get; set; }
}

/// <summary>Phase0 <c>Checkpoint</c>.</summary>
[SszContainer]
public partial class Checkpoint
{
    public ulong Epoch { get; set; }

    public Hash256? Root { get; set; }
}

/// <summary>Phase0 <c>SigningData</c>.</summary>
[SszContainer]
public partial class SigningData
{
    public Hash256? ObjectRoot { get; set; }

    public Hash256? Domain { get; set; }
}

/// <summary>Phase0 <c>BeaconBlockHeader</c>.</summary>
[SszContainer]
public partial class BeaconBlockHeader
{
    public ulong Slot { get; set; }

    public ulong ProposerIndex { get; set; }

    public Hash256? ParentRoot { get; set; }

    public Hash256? StateRoot { get; set; }

    public Hash256? BodyRoot { get; set; }
}

/// <summary>Phase0 <c>SignedBeaconBlockHeader</c>.</summary>
[SszContainer]
public partial class SignedBeaconBlockHeader
{
    public BeaconBlockHeader? Message { get; set; }

    public BlsSignature Signature { get; set; }
}

/// <summary>Phase0 <c>Eth1Data</c>.</summary>
[SszContainer]
public partial class Eth1Data
{
    public Hash256? DepositRoot { get; set; }

    public ulong DepositCount { get; set; }

    public Hash256? BlockHash { get; set; }
}

/// <summary>Phase0 <c>Validator</c>.</summary>
/// <remarks>
/// Treated as immutable by convention: the setters exist only for the generated SSZ <c>Decode</c>.
/// State transitions replace <see cref="Validator"/> instances instead of mutating them.
/// </remarks>
[SszContainer]
public partial class Validator
{
    public BlsPublicKey Pubkey { get; set; }

    public Hash256? WithdrawalCredentials { get; set; }

    public ulong EffectiveBalance { get; set; }

    public bool Slashed { get; set; }

    public ulong ActivationEligibilityEpoch { get; set; }

    public ulong ActivationEpoch { get; set; }

    public ulong ExitEpoch { get; set; }

    public ulong WithdrawableEpoch { get; set; }
}

/// <summary>Capella <c>HistoricalSummary</c>.</summary>
[SszContainer]
public partial class HistoricalSummary
{
    public Hash256? BlockSummaryRoot { get; set; }

    public Hash256? StateSummaryRoot { get; set; }
}
