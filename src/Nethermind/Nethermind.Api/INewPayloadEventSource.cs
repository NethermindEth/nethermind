// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Api
{
    /// <summary>
    /// Event arguments for when an engine_newPayloadVX call successfully processes a block.
    /// </summary>
    public class NewPayloadProcessedEventArgs(Hash256 hash, long number, TimeSpan processingTime) : EventArgs
    {
        public Hash256 Hash { get; } = hash;
        public long Number { get; } = number;
        public TimeSpan ProcessingTime { get; } = processingTime;
    }

    /// <summary>
    /// Provides events for engine API newPayload processing.
    /// </summary>
    public interface INewPayloadEventSource
    {
        /// <summary>
        /// Raised when a new payload has been successfully processed with a Valid result.
        /// </summary>
        event EventHandler<NewPayloadProcessedEventArgs>? NewPayloadProcessed;
    }
}
