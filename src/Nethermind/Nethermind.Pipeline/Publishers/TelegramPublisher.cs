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

using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Nethermind.Serialization.Json;

namespace Nethermind.Pipeline.Publishers
{
    public class TelegramPublisher : IPublisher
    {
        private readonly IJsonSerializer _serializer;
        private readonly string _chatId;
        private string _botToken;
        private readonly HttpClient _httpClient;
        
        public TelegramPublisher(IJsonSerializer serializer, string chatId)
        {
            _serializer = serializer;
            _chatId = chatId;
            _botToken = LoadBotToken();
            _httpClient = new HttpClient();
        }
        
        public void SubscribeToData<T>(T data)
        {
            Task.Run(() => SendMessageAsync(data));
        }

        private async Task SendMessageAsync(object data)
        {
            var message = _serializer.Serialize(data);

            var url = $"https://api.telegram.org/bot{_botToken}/sendMessage?chat_id=-{_chatId}&text={message}";
            await _httpClient.GetAsync(url);
        }

        private string LoadBotToken()
        {
            return "1894135076:AAHmdMwkLia8FzUiDBsZ-h3kqsBIzeGa4y8"; //this is just a test bot, will do a proper secret token after tests are done and we will create another bot  
        }
    }
}