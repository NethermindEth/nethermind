// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Legacy.Bellatrix;

/// <summary>Bellatrix <c>ExecutionPayload</c>.</summary>
[SszContainer]
public partial class ExecutionPayload : Nethermind.BeaconChain.Legacy.Capella.ExecutionPayload
{
    [SszIgnore]
    public new SszWithdrawal[]? Withdrawals { get => base.Withdrawals; set => base.Withdrawals = value; }
}

/// <summary>Bellatrix <c>ExecutionPayloadHeader</c>.</summary>
[SszContainer]
public partial class ExecutionPayloadHeader : Nethermind.BeaconChain.Legacy.Capella.ExecutionPayloadHeader
{
    [SszIgnore]
    public new Hash256? WithdrawalsRoot { get => base.WithdrawalsRoot; set => base.WithdrawalsRoot = value; }
}
