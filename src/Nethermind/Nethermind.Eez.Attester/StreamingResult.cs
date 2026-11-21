// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Eez.Prove.V2;

namespace Nethermind.Eez.Attester;

internal sealed record StreamingResult(ExecutedStreamingBlock? Block, Proof? Proof);
