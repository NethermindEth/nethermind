// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Config
{
    public interface IConfigProvider
    {
        /// <summary>
        /// Gets a parsed configuration type. It contains the data from all the config sources combined.
        /// </summary>
        /// <typeparam name="T">Type of the configuration interface.</typeparam>
        /// <returns>The configuration object.</returns>
        T GetConfig<T>() where T : IConfig;

        /// <summary>
        /// Gets the value exactly in the format of the configuration data source.
        /// </summary>
        /// <param name="category">Configuration category (e.g. Init).</param>
        /// <param name="name">Name of the configuration property.</param>
        /// <returns></returns>
        object GetRawValue(string category, string name);
    }
}
