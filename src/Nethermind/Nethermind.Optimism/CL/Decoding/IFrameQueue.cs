// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Optimism.CL.Decoding;

public interface IFrameQueue
{
    BatchV1[]? ConsumeFrame(Frame frame);
    void Clear();
}
