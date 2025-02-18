// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Optimism.CL.Decoding;

namespace Nethermind.Optimism.CL.Decoding;

public interface IChannelStorage
{
    void ConsumeFrames(Frame[] frames);
    BatchV1[]? GetReadyBatches();
}
