// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Processing.ParallelProcessing;

public interface IIsTracing;

public readonly struct NotTracing : IIsTracing;

public readonly struct IsTracing : IIsTracing;
