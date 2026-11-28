// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;

namespace Nethermind.Consensus.Processing;

/// <summary>The verdict on one queued copy of a block, from <see cref="IBlockProcessingQueue.EnqueueAndWaitForVerdictAsync"/>.</summary>
/// <param name="Result">
/// <see cref="ProcessingResult.Success"/> or <see cref="ProcessingResult.InclusionListUnsatisfied"/> once the copy is
/// executed and validated, before it is committed; any other result is the copy's removal.
/// </param>
/// <param name="Message">The removal's message, if any.</param>
/// <param name="Exception">The removal's exception, if any.</param>
/// <param name="Removed">
/// Completes with the copy's removal result once it has left the queue: anything but <see cref="ProcessingResult.Success"/>
/// or <see cref="ProcessingResult.InclusionListUnsatisfied"/> after a verdict given before the commit means the commit
/// did not follow. Already complete when <paramref name="Result"/> is the removal itself.
/// </param>
public sealed record BlockVerdict(ProcessingResult Result, string? Message, Exception? Exception, Task<ProcessingResult> Removed);
