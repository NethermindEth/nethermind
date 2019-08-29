/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;

namespace Nethermind.Store
{
    public interface IDb : IDisposable
    {
        /// <summary>
        /// Name of the database for logging purposes only.
        /// </summary>
        string Name { get; }
        
        /// <summary>
        /// The most common method of retrieving data from the database or updating data in the database.
        /// Simple key value store is expected as a backing DB for this interface.
        /// </summary>
        /// <param name="key"></param>
        byte[] this[byte[] key] { get; set; }
        
        /// <summary>
        /// Use only if batched updates introduce performance improvement when writing to the database.
        /// Otherwise implementations can just do nothing here.
        /// You are guaranteed to receive exactly one StartBatch before every CommitBatch.
        /// You are guaranteed to receive exactly on CommitBatch after each StartBatch.
        /// </summary>
        void StartBatch();
        
        /// <summary>
        /// Use only if batched updates introduce performance improvement when writing to the database.
        /// Otherwise implementations can just do nothing here.
        /// You are guaranteed to receive exactly one StartBatch before every CommitBatch.
        /// You are guaranteed to receive exactly on CommitBatch after each StartBatch.
        /// </summary>
        void CommitBatch();

        /// <summary>
        /// Do not implement this - to be reviewed / removed from this interface
        /// </summary>
        /// <param name="key">ignore</param>
        void Remove(byte[] key);
        
        /// <summary>
        /// Do not implement this - to be reviewed / removed from this interface
        /// </summary>
        /// <returns>ignore</returns>
        byte[][] GetAll();
        
        /// <summary>
        /// Do not implement this - to be reviewed / removed from this interface
        /// </summary>
        /// <param name="key">ignore</param>
        /// <returns>ignore</returns>
        bool KeyExists(byte[] key);
    }

    public interface IDbWithSpan : IDisposable
    {
        Span<byte> GetSpan(byte[] key);
        void DangerousReleaseMemory(in Span<byte> span);
    }
}