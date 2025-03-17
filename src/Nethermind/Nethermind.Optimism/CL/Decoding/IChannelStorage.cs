// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Optimism.CL.Decoding;

public interface IChannelStorage
{
    BatchV1[]? GetReadyBatches();
    void ConsumeFrame(Frame frames);
}
