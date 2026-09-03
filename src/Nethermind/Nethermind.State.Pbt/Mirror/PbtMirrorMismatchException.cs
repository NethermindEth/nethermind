// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Pbt.Mirror;

/// <summary>Thrown when the mirrored PBT state answers a read differently from the authoritative backend.</summary>
public class PbtMirrorMismatchException(string message) : Exception(message)
{
}
