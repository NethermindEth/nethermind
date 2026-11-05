// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Rlp;

public interface IRlpWrapper
{
    int RlpLength { get; }
    void Write<TWriter>(ref TWriter writer)
        where TWriter : struct, IRlpWriteBackend, allows ref struct;
}
