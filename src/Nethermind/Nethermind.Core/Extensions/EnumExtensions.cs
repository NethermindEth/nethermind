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

using System;
using System.Collections.Generic;
using System.Linq;
using FastEnumUtility;

namespace Nethermind.Core.Extensions;

public static class EnumExtensions
{
    /// <summary>
    /// Returns all combinations of enum values of type <see cref="T"/>.
    /// </summary>
    /// <typeparam name="T">Type of enumeration.</typeparam>
    /// <returns>All combinations of defined enum values.</returns>
    /// <remarks>
    /// For normal enums this is equivalent to all defined values.
    /// For <see cref="FlagsAttribute"/> enums this produces all combination of defined values.
    /// </remarks>
    public static IReadOnlyList<T> AllValuesCombinations<T>() where T : struct, Enum
    {
        // The return type of Enum.GetValues is Array but it is effectively int[] per docs
        // This bit converts to int[]
        IReadOnlyList<T> values = FastEnum.GetValues<T>();

        if (!typeof(T).GetCustomAttributes(typeof(FlagsAttribute), false).Any())
        {
            // We don't have flags so just return the result of GetValues
            return values;
        }

        // TODO: in .net 7 rewrite with generic INumber based on FastEnum.GetUnderlyingType<T>()
        int[] valuesBinary = values.Cast<int>().ToArray();

        int[] valuesInverted = valuesBinary.Select(v => ~v).ToArray();
        int max = 0;
        for (int i = 0; i < valuesBinary.Length; i++)
        {
            max |= valuesBinary[i];
        }

        List<T> result = new();
        for (int i = 0; i <= max; i++)
        {
            int unaccountedBits = i;
            for (int j = 0; j < valuesInverted.Length; j++)
            {
                // This step removes each flag that is set in one of the Enums thus ensuring that an Enum with missing bits won't be passed an int that has those bits set
                unaccountedBits &= valuesInverted[j];
                if (unaccountedBits == 0)
                {
                    result.Add((T)(object)i);
                    break;
                }
            }
        }

        //Check for zero
        try
        {
            if (string.IsNullOrEmpty(Enum.GetName(typeof(T), (T)(object)0)))
            {
                result.Remove((T)(object)0);
            }
        }
        catch
        {
            result.Remove((T)(object)0);
        }

        return result;
    }
}
