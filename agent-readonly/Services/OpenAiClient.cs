using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AgentReadonly.Services
{
    public class OpenAiClient
    {
        private const string BaseUrl = "https://api.openai.com/v1";

        private readonly string apiKey;
        private readonly HttpClient httpClient;
        private readonly JavaScriptSerializer serializer;

        public OpenAiClient(string apiKey)
        {
            this.apiKey = apiKey;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(5);
            serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        }

        public async Task<Dictionary<string, object>> CreateResponseAsync(Dictionary<string, object> request, CancellationToken cancellationToken)
        {
            string json = serializer.Serialize(request);
            using (HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/responses"))
            {
                message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                message.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using (HttpResponseMessage response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false))
                {
                    string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException("OpenAI status " + (int)response.StatusCode + ": " + text);

                    object parsed = serializer.DeserializeObject(text);
                    Dictionary<string, object> map = parsed as Dictionary<string, object>;
                    if (map == null)
                        throw new InvalidOperationException("OpenAI response was not a JSON object.");
                    return map;
                }
            }
        }
    }
}
