//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

namespace Nethermind.Api.Extensions;

/// <summary>
/// Assemblies containing instances of this interface will be the ones
/// used to load custom initialization steps.
/// </summary>
public interface IInitializationPlugin : INethermindPlugin
{
    /// <summary>
    /// This method will be called on the plugin instance
    /// decide whether or not we need to run initialization steps
    /// defined in its assembly. It receives the api to be able to
    /// look at the config.
    /// </summary>
    bool ShouldRunSteps(INethermindApi api);
}
