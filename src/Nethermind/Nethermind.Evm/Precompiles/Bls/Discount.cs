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

using System.Collections.Generic;

namespace Nethermind.Evm.Precompiles.Bls
{
    /// <summary>
    /// https://eips.ethereum.org/EIPS/eip-2537
    /// </summary>
    internal static class Discount
    {
        public static int For(int k)
        {
            return k switch
            {
                0 => 0,
                var x when x >= 128 => 174,
                _ => _discountTable[k]
            };
        }
        
        private static Dictionary<int, int> _discountTable = new()
        {
            {1, 1200}, {2, 888}, {3, 764}, {4, 641}, {5, 594}, {6, 547}, {7, 500}, {8, 453},
            {9, 438}, {10, 423}, {11, 408}, {12, 394}, {13, 379}, {14, 364}, {15, 349}, {16, 334},
            {17, 330}, {18, 326}, {19, 322}, {20, 318}, {21, 314}, {22, 310}, {23, 306}, {24, 302},
            {25, 298}, {26, 294}, {27, 289}, {28, 285}, {29, 281}, {30, 277}, {31, 273}, {32, 269},
            {33, 268}, {34, 266}, {35, 265}, {36, 263}, {37, 262}, {38, 260}, {39, 259}, {40, 257},
            {41, 256}, {42, 254}, {43, 253}, {44, 251}, {45, 250}, {46, 248}, {47, 247}, {48, 245},
            {49, 244}, {50, 242}, {51, 241}, {52, 239}, {53, 238}, {54, 236}, {55, 235}, {56, 233},
            {57, 232}, {58, 231}, {59, 229}, {60, 228}, {61, 226}, {62, 225}, {63, 223}, {64, 222},
            {65, 221}, {66, 220}, {67, 219}, {68, 219}, {69, 218}, {70, 217}, {71, 216}, {72, 216},
            {73, 215}, {74, 214}, {75, 213}, {76, 213}, {77, 212}, {78, 211}, {79, 211}, {80, 210},
            {81, 209}, {82, 208}, {83, 208}, {84, 207}, {85, 206}, {86, 205}, {87, 205}, {88, 204},
            {89, 203}, {90, 202}, {91, 202}, {92, 201}, {93, 200}, {94, 199}, {95, 199}, {96, 198},
            {97, 197}, {98, 196}, {99, 196}, {100, 195}, {101, 194}, {102, 193}, {103, 193}, {104, 192},
            {105, 191}, {106, 191}, {107, 190}, {108, 189}, {109, 188}, {110, 188}, {111, 187}, {112, 186},
            {113, 185}, {114, 185}, {115, 184}, {116, 183}, {117, 182}, {118, 182}, {119, 181}, {120, 180},
            {121, 179}, {122, 179}, {123, 178}, {124, 177}, {125, 176}, {126, 176}, {127, 175}, {128, 174}
        };
    }
}
