// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Xdc;

public class NetworkInformation
{
    public UInt256? NetworkId { get; set; }
    public Address? XDCValidatorAddress { get; set; }
    public Address? RelayerRegistrationAddress { get; set; }
    public Address? XDCXListingAddress { get; set; }
    public Address? XDCZAddress { get; set; }
    public Address? LendingAddress { get; set; }
    public XDPoSConfig? ConsensusConfigs { get; set; }
}
