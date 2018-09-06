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

using System.Text.RegularExpressions;

namespace Nethermind.LibSolc.DataModel
{
  public class CompilerInput
  {
    private static string ContractName { get; set; }
    private static string Contract { get; set; }
    private static string Json { get; set; }
    private static string EvmVersion { get; set; }
    private static string Optimize { get; set; }
    private static uint? Runs { get; set; }
    
    public CompilerInput(string _contract, string _evmVersion, bool _optimize, uint? _runs)
    {
      Contract = _contract;
      EvmVersion = _evmVersion;
      Optimize = _optimize ? "true" : "false";
      Runs = _runs ?? 200;
      
      Match match = Regex.Match(_contract,@"contract (.*?) ");
      ContractName = match.Groups[1].Value;
      
      /*
       * In the 'settings' section, if the optimizer setting is omitted, the compiler library code uses defaults
       * of false for the 'enabled' field and 200 for the 'runs'
       * See libsolidity/interface/StandardCompiler.cpp in the solidity source code
       */
      Json =
        $@"{{
          ""language"": ""Solidity"",
          ""sources"":
          {{
            ""{ContractName}"": {{ ""content"": ""{Contract}"" }}
          }},
          ""settings"":
          {{ 
            ""optimizer"": {{ 
              ""enabled"": {Optimize},
              ""runs"": {Runs.Value}
            }},
            ""evmVersion"": ""{EvmVersion}"",
            ""metadata"": {{ 
              ""useLiteralContent"": true
            }},
            ""outputSelection"": {{ 
              ""*"": {{ 
                ""*"": [ ""metadata"", ""evm.bytecode"", ""evm.gasEstimates"" ]
              }},
              ""def"": {{ 
                ""{ContractName}"": [ ""abi"" ]
              }}
            }}
          }}
        }}";
    }

    public string Value()
    {
      return Json;
    }

  }

}