// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>The scratch replay base cannot be used and no retry will change that: its rows, checkpoint or identity
/// disagree with the chain it was imported from, so the replay stops until the scratch directory is preserved or
/// removed and a fresh import starts.</summary>
public sealed class ScratchStateUnusableException(string message) : Exception(message);
