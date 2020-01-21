//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

namespace Nethermind.Runner.Ethereum.Subsystems
{
    public enum EthereumSubsystemState
    {
        /// <summary>
        /// Enabled and running
        /// </summary>
        Running,
        /// <summary>
        /// Waiting for dependencies and scheduling
        /// </summary>
        AwaitingInitialization,
        /// <summary>
        /// Initializing (it means that all dependencies are <value>Initialized</value>, <value>Disabled</value> or <value>Running</value>
        /// </summary>
        Initializing,
        /// <summary>
        /// Was initializing but failed
        /// </summary>
        Failed,
        /// <summary>
        /// Is disabled in configuration and initialization will be skipped and all dependent items will skip this dependency.
        /// </summary>
        Disabled
    }
}