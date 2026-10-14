// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;

internal enum SequenceKind { Vector, List, ProgressiveList }
internal enum ContainerKind { Container, ProgressiveContainer }

internal abstract record SszSchema(int? FixedSize);
internal sealed record BasicSchema(string Identity, int Size) : SszSchema(Size);
internal sealed record SequenceSchema(SequenceKind Kind, SszSchema Element, ulong? Bound, int? Size) : SszSchema(Size);
internal sealed record BitSequenceSchema(SequenceKind Kind, ulong? Bound, int? Size) : SszSchema(Size);
internal sealed record ContainerSchema(
    ContainerKind Kind,
    ImmutableArray<SszField> Fields,
    int FixedSectionSize,
    int? Size,
    ImmutableArray<byte> ActiveFields,
    int ActiveFieldCount) : SszSchema(Size);
internal sealed record UnionSchema(ImmutableArray<SszSchema> Options) : SszSchema((int?)null);
internal sealed record CodecParameterSchema(string Identity) : SszSchema((int?)null);
internal sealed record SszField(string Name, SszSchema Schema, int? Index = null, int FixedOffset = 0);
