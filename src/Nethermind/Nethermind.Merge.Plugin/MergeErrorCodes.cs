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

namespace Nethermind.Merge.Plugin
{
    // Error codes spec: https://github.com/ethereum/execution-apis/blob/v1.0.0-alpha.5/src/engine/specification.md#errors
    public static class MergeErrorCodes
    {
        public const int None = 0;

        public const int UnavailablePayloadV1 = -32001;
        
        public const int InvalidForkchoiceState = -38002;

        public const int InvalidPayloadAttributes = -38003;
    }
}
