using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Diagnostics;
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
            AppLog.Info("OpenAI request start: POST /responses bytes=" + Encoding.UTF8.GetByteCount(json));
            AppLog.Debug("OpenAI request payload: " + AppLog.Truncate(SanitizeRequestJson(request)));
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                using (HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/responses"))
                {
                    message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                    message.Content = new StringContent(json, Encoding.UTF8, "application/json");

                    using (HttpResponseMessage response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false))
                    {
                        string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        stopwatch.Stop();
                        AppLog.Info("OpenAI response: status=" + (int)response.StatusCode + " elapsed_ms=" + stopwatch.ElapsedMilliseconds + " bytes=" + Encoding.UTF8.GetByteCount(text));
                        AppLog.Debug("OpenAI response body: " + AppLog.Truncate(text));
                        if (!response.IsSuccessStatusCode)
                        {
                            AppLog.Warn("OpenAI non-success response: status=" + (int)response.StatusCode + " body=" + AppLog.Truncate(text));
                            throw new InvalidOperationException("OpenAI status " + (int)response.StatusCode + ": " + text);
                        }

                        object parsed = serializer.DeserializeObject(text);
                        Dictionary<string, object> map = parsed as Dictionary<string, object>;
                        if (map == null)
                        {
                            AppLog.Warn("OpenAI response was not a JSON object.");
                            throw new InvalidOperationException("OpenAI response was not a JSON object.");
                        }
                        return map;
                    }
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                if (ex is OperationCanceledException)
                    AppLog.Warn("OpenAI request canceled: elapsed_ms=" + stopwatch.ElapsedMilliseconds);
                else
                    AppLog.Error("OpenAI request failed: elapsed_ms=" + stopwatch.ElapsedMilliseconds, ex);
                throw;
            }
        }

        private string SanitizeRequestJson(Dictionary<string, object> request)
        {
            Dictionary<string, object> copy = new Dictionary<string, object>(request);
            object instructions;
            if (copy.TryGetValue("instructions", out instructions) && instructions != null)
                copy["instructions"] = "<instructions chars=" + Convert.ToString(instructions).Length + ">";
            return serializer.Serialize(copy);
        }
    }
}
