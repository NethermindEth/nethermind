// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Rlp;

public interface IRlpWrapper
{
    int RlpLength { get; }
    void Write<TBackend>(ref ValueRlpWriter<TBackend> writer)
        where TBackend : IValueRlpWriteBackend, allows ref struct;
}
