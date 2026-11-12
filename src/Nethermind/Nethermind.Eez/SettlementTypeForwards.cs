// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Follower.RollupX;

[assembly: TypeForwardedTo(typeof(L1Batch))]
[assembly: TypeForwardedTo(typeof(SettledRoot))]
[assembly: TypeForwardedTo(typeof(L1Settlement))]
[assembly: TypeForwardedTo(typeof(SettlementAttribution))]
[assembly: TypeForwardedTo(typeof(L1SourceIncompleteException))]
[assembly: TypeForwardedTo(typeof(ClaimedChain))]
[assembly: TypeForwardedTo(typeof(CarrierAttribution))]
