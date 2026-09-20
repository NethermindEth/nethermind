// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Nethermind.Core.Test")]
// Lets the beacon chain plugin reach the one already-loaded KZG trusted setup handle
// (KzgPolynomialCommitments.CkzgSetup) instead of loading a second copy of its own.
[assembly: InternalsVisibleTo("Nethermind.BeaconChain")]
