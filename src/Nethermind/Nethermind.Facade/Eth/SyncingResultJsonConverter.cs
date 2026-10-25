// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.Facade.Eth;

public class SyncingResultJsonConverter : JsonConverter<SyncingResult>
{
    public override SyncingResult Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) => throw new NotSupportedException();

    public override void Write(
        Utf8JsonWriter writer,
        SyncingResult value,
        JsonSerializerOptions options)
    {
        if (!value.IsSyncing)
        {
            writer.WriteBooleanValue(false);
            return;
        }

        TypeInfoJsonSerializer.Serialize(writer, new Result
        {
            StartingBlock = value.StartingBlock,
            CurrentBlock = value.CurrentBlock,
            HighestBlock = value.HighestBlock,
            SyncedAccounts = value.SyncedAccounts,
            SyncedStorage = value.SyncedStorage,
            SyncedBytecodes = value.SyncedBytecodes,
            HealedTrienodes = value.HealedTrienodes,
            HealedBytecodes = value.HealedBytecodes,
            WarpChunksAmount = value.WarpChunksAmount,
            WarpChunksProcessed = value.WarpChunksProcessed
        }, options);
    }

    internal struct Result
    {
        public ulong StartingBlock { get; set; }
        public ulong CurrentBlock { get; set; }
        public ulong HighestBlock { get; set; }
        public ulong? SyncedAccounts { get; set; }
        public ulong? SyncedStorage { get; set; }
        public ulong? SyncedBytecodes { get; set; }
        public ulong? HealedTrienodes { get; set; }
        public ulong? HealedBytecodes { get; set; }
        public ulong? WarpChunksAmount { get; set; }
        public ulong? WarpChunksProcessed { get; set; }
    }
}
