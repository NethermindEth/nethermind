// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Stateless.Execution.IO;

public static partial class TrustedInput
{
    private static partial byte[] Item(ReadOnlySpan<byte> data, int start, int length) => data.Slice(start, length).ToArray();

    private static partial ReadOnlyMemory<byte> Code(ReadOnlySpan<byte> data, int start, int length) =>
        ExecutableCodeMemory.Copy(data.Slice(start, length));

    private static partial bool TryTakePrehashedCode(ReadOnlySpan<byte> code, int index, out ValueHash256 hash)
    {
        hash = default;
        return false;
    }

    private static unsafe partial IOwnedReadOnlyList<byte[]> ReadStateNodes(ReadOnlySpan<byte> data) => new ItemList(ReadItems(data, StateNodesList, 0));
}
