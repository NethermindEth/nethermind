// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Int256;
using NUnit.Framework;

/// <inheritdoc cref="JsonMetadataCoverageBase"/>
/// <remarks>Outside any namespace, so it covers every fixture in the assembly; named apart from the fixtures of assemblies that see its internals.</remarks>
[SetUpFixture]
internal class CoreJsonMetadataCoverage() : JsonMetadataCoverageBase(TestOnly)
{
    /// <summary>Values the converter tests round trip through the node's options as roots of their own.</summary>
    private static readonly Dictionary<Type, string> TestOnly = new()
    {
        [typeof(Dictionary<UInt256, Hash256>)] = "converter round trip",
        [typeof(Dictionary<string, List<Dictionary<string, Dictionary<UInt256, Dictionary<string, Dictionary<UInt256, Hash256>>>>>>)] = "converter round trip",
        [typeof(Dictionary<AddressAsKey, int>)] = "converter round trip",
        [typeof(Dictionary<Address, int>)] = "converter round trip",
        [typeof(IPAddress)] = "converter round trip",
        [typeof(StorageCell)] = "converter round trip",
        [typeof(Nethermind.Core.BlockAccessLists.ReadOnlySlotChanges)] = "converter round trip",
        [typeof(Nethermind.Core.BlockAccessLists.GeneratedSlotChanges)] = "converter round trip",
        [typeof(Nethermind.Core.BlockAccessLists.BalanceChange)] = "converter round trip",
        [typeof(Nethermind.Core.BlockAccessLists.SlotChangeAtIndex)] = "converter round trip",
    };
}