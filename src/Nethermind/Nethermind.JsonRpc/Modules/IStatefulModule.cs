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

namespace Nethermind.JsonRpc.Modules
{
    /// <summary>
    /// The module that should have its state reset after every request and cannot be reused concurrently by various requests.
    /// Module pooling should be used. See <see cref="IRpcModulePool{T}"/>.
    /// </summary>
    public interface IStatefulModule : IModule
    {
        /// <summary>
        /// Resets any state so the module can be reused by the next request.
        /// </summary>
        void ResetState();
    }
}