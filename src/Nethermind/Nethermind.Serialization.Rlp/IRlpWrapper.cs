// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp;

public interface IRlpWrapper
{
    int RlpLength { get; }
    void Write(ref RlpWriter writer);
}
