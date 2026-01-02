// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Buffers;

public interface ICappedArrayPool
{
    CappedArray<byte> Rent(int size);

    void Return(in CappedArray<byte> buffer);
}
