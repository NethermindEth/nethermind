// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.BeaconChain.ForkChoice;

/// <summary>Thrown when a proto-array fork-choice invariant is violated.</summary>
/// <remarks>Covers the failure modes of Lighthouse's <c>proto_array::Error</c> enum.</remarks>
public sealed class ProtoArrayException(string message) : Exception(message);
