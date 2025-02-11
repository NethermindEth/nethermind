// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Optimism.CL.Derivation;

public interface IFrameQueue
{
    void ConsumeFrame(Frame frames);
    bool IsReady();
    byte[] BuildChannel();
}
