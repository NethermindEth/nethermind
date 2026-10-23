// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Eez.Sequencer;

/// <summary>The sequencer cannot go on: a fault of its own production or setup, not something L1 settled.</summary>
public sealed class EezSequencerException(string message) : Exception(message);
