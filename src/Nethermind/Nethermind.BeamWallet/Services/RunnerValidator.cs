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
// 

using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace Nethermind.BeamWallet.Services
{
    class RunnerValidator : IRunnerValidator
    {
        private readonly HttpClient _httpClient;
        private string _url;

        public RunnerValidator(HttpClient httpClient, string url)
        {
            _httpClient = httpClient;
            _url = url;
        }
        
        public async Task<bool> IsRunningAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync(_url);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                // ignored
            }

            return false;
        }
    }
}
