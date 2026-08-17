// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Evm
{
    public static class MemoryAllowance
    {
        public static int CodeCacheSize { get; } = 4_096 + 1_024;

        // Halved when MaxStreamRetainedBytes doubled to 512 KiB, keeping the worst-case retained-bytes ceiling flat.
        // Experiment knob: concurrent RPC load multiplies the simultaneously-hot code set, and a
        // set-associative cache starts conflict-evicting well before global capacity is reached —
        // suspected driver of the RunByteCode fallback growing 7x per request at 300rps. Env-tunable
        // so same-build A/B arms can size it; the default stays the documented ceiling.
        public static int InstructionStreamCacheSize { get; } =
            int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_INSTRUCTION_STREAM_CACHE_SIZE"), out int size) && size > 0
                ? size
                : 1_024;
    }
}
