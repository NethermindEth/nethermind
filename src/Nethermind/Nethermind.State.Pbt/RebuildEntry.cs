// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>One complete-key leaf record to include in a rebuilt EIP-8297 tree.</summary>
public readonly record struct RebuildEntry(PbtStorageFullKey Key, ValueHash256 Leaf);
