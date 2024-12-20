// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.FastRlp.Generator;

namespace Nethermind.Serialization.FastRlp.Test;

[RlpSerializable]
public record Player(int Id, string Username);

