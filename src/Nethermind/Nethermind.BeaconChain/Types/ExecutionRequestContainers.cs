// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Types;

/// <summary>Electra <c>DepositRequest</c> (EIP-6110).</summary>
[SszContainer]
public partial class DepositRequest
{
    public BlsPublicKey Pubkey { get; set; }

    public Hash256? WithdrawalCredentials { get; set; }

    public ulong Amount { get; set; }

    public BlsSignature Signature { get; set; }

    public ulong Index { get; set; }
}

/// <summary>Electra <c>WithdrawalRequest</c> (EIP-7002).</summary>
[SszContainer]
public partial class WithdrawalRequest
{
    public Address? SourceAddress { get; set; }

    public BlsPublicKey ValidatorPubkey { get; set; }

    public ulong Amount { get; set; }
}

/// <summary>Electra <c>ConsolidationRequest</c> (EIP-7251).</summary>
[SszContainer]
public partial class ConsolidationRequest
{
    public Address? SourceAddress { get; set; }

    public BlsPublicKey SourcePubkey { get; set; }

    public BlsPublicKey TargetPubkey { get; set; }
}

/// <summary>Electra <c>ExecutionRequests</c> (EIP-7685).</summary>
[SszContainer]
public partial class ExecutionRequests
{
    [SszList(8192)]
    public DepositRequest[]? Deposits { get; set; }

    [SszList(16)]
    public WithdrawalRequest[]? Withdrawals { get; set; }

    [SszList(2)]
    public ConsolidationRequest[]? Consolidations { get; set; }
}

/// <summary>Electra <c>PendingDeposit</c>.</summary>
[SszContainer]
public partial class PendingDeposit
{
    public BlsPublicKey Pubkey { get; set; }

    public Hash256? WithdrawalCredentials { get; set; }

    public ulong Amount { get; set; }

    public BlsSignature Signature { get; set; }

    public ulong Slot { get; set; }
}

/// <summary>Electra <c>PendingPartialWithdrawal</c>.</summary>
[SszContainer]
public partial class PendingPartialWithdrawal
{
    public ulong ValidatorIndex { get; set; }

    public ulong Amount { get; set; }

    public ulong WithdrawableEpoch { get; set; }
}

/// <summary>Electra <c>PendingConsolidation</c>.</summary>
[SszContainer]
public partial class PendingConsolidation
{
    public ulong SourceIndex { get; set; }

    public ulong TargetIndex { get; set; }
}
