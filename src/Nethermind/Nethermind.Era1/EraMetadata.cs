// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Era1;

public class EraMetadata
{
    public long Start { get; }
    public long End => Start + Count - 1;
    public long Count { get; }
    public long Length { get; }

    public EraMetadata(long start, long count, long length)
    {
        Start = start;
        Count = count;
        Length = length;
    }
}
