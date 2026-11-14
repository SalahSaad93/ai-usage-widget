using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AiUsageWidget.Core
{
    public sealed class HttpResponse
    {
        public int Status;
        public string Text;
        public byte[] Bytes;
    }

    // One HttpClient for the process; TLS 1.2 forced and a stable User-Agent everywhere.
    public static class Http
    {
        public static readonly HttpClient Client = CreateClient();

        static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ai-usage-widget");
            return client;
        }

        public static Task<HttpResponse> GetTextAsync(string url, KeyValuePair<string, string>[] headers, CancellationToken ct)
        {
            return SendAsync(url, headers, ct, false);
        }

        public static Task<HttpResponse> GetBytesAsync(string url, KeyValuePair<string, string>[] headers, CancellationToken ct)
        {
            return SendAsync(url, headers, ct, true);
        }

        static async Task<HttpResponse> SendAsync(string url, KeyValuePair<string, string>[] headers, CancellationToken ct, bool asBytes)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (headers != null)
                {
                    foreach (KeyValuePair<string, string> h in headers)
                        if (!string.IsNullOrEmpty(h.Value))
                            req.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
                using (HttpResponseMessage resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    var result = new HttpResponse();
                    result.Status = (int)resp.StatusCode;
                    if (resp.Content != null)
                    {
                        // The body read takes no token: a server that sends headers and then
                        // stalls would hang it. Disposing the response on cancel aborts it.
                        using (ct.Register(delegate { resp.Dispose(); }))
                        {
                            try
                            {
                                if (asBytes) result.Bytes = await resp.Content.ReadAsByteArrayAsync();
                                else result.Text = await resp.Content.ReadAsStringAsync();
                            }
                            catch (Exception)
                            {
                                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                                throw;
                            }
                        }
                        ct.ThrowIfCancellationRequested();
                    }
                    return result;
                }
            }
        }
    }
}
