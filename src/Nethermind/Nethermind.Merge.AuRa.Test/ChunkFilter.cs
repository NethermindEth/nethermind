// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Partitions this assembly's tests by TEST_CHUNK so the heavy flat-DB run can be
// chunked on slower CI runners. See Nethermind.Core.Test.ChunkFilterAttribute.
[assembly: Nethermind.Core.Test.ChunkFilterAttribute]
