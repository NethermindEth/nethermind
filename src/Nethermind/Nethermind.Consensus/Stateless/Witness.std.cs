// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Consensus.Stateless;

public static partial class WitnessExtensions
{
    private static partial BlockHeader? DecodeHeader(ref RlpReader reader, bool isParent) => Decoder.Decode(ref reader);
}
