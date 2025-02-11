// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Optimism.CL.Derivation;

public interface IChannelStorage
{
    void ConsumeFrames(Frame[] frames);
    BatchV1[]? GetReadyBatches();
}
