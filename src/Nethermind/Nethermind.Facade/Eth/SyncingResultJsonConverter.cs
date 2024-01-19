// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

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

        JsonSerializer.Serialize(writer, new Result
        {
            StartingBlock = value.StartingBlock,
            CurrentBlock = value.CurrentBlock,
            HighestBlock = value.HighestBlock
        }, options);
    }

    private struct Result
    {
        public long StartingBlock { get; set; }
        public long CurrentBlock { get; set; }
        public long HighestBlock { get; set; }
    }
}
