// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Types;

/// <summary>Phase0 <c>DepositData</c>.</summary>
[SszContainer]
public partial class DepositData
{
    public BlsPublicKey Pubkey { get; set; }

    public Hash256? WithdrawalCredentials { get; set; }

    public ulong Amount { get; set; }

    public BlsSignature Signature { get; set; }
}

/// <summary>Phase0 <c>DepositMessage</c>.</summary>
[SszContainer]
public partial class DepositMessage
{
    public BlsPublicKey Pubkey { get; set; }

    public Hash256? WithdrawalCredentials { get; set; }

    public ulong Amount { get; set; }
}

/// <summary>Phase0 <c>Deposit</c>.</summary>
[SszContainer]
public partial class Deposit
{
    /// <remarks>Merkle path of length <c>DEPOSIT_CONTRACT_TREE_DEPTH + 1</c>.</remarks>
    [SszVector(33)]
    public Hash256[]? Proof { get; set; }

    public DepositData? Data { get; set; }
}

/// <summary>Phase0 <c>VoluntaryExit</c>.</summary>
[SszContainer]
public partial class VoluntaryExit
{
    public ulong Epoch { get; set; }

    public ulong ValidatorIndex { get; set; }
}

/// <summary>Phase0 <c>SignedVoluntaryExit</c>.</summary>
[SszContainer]
public partial class SignedVoluntaryExit
{
    public VoluntaryExit? Message { get; set; }

    public BlsSignature Signature { get; set; }
}

/// <summary>Capella <c>BLSToExecutionChange</c>.</summary>
[SszContainer]
public partial class BlsToExecutionChange
{
    public ulong ValidatorIndex { get; set; }

    public BlsPublicKey FromBlsPubkey { get; set; }

    public Address? ToExecutionAddress { get; set; }
}

/// <summary>Capella <c>SignedBLSToExecutionChange</c>.</summary>
[SszContainer]
public partial class SignedBlsToExecutionChange
{
    public BlsToExecutionChange? Message { get; set; }

    public BlsSignature Signature { get; set; }
}
