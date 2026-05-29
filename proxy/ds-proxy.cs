using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

class DsProxy
{
    const string TARGET_HOST = "api.deepseek.com";
    const string TARGET_PATH = "/anthropic";

    static readonly object lockObj = new object();
    static List<Dictionary<string, object>> pendingThinkingBlocks = new List<Dictionary<string, object>>();

    // SSE parsing state
    static Dictionary<int, StringBuilder> thinkingAccum = new Dictionary<int, StringBuilder>();

    static JavaScriptSerializer json = new JavaScriptSerializer();

    static void Main(string[] args)
    {
        SetSslProtocols();

        int port = 16889;
        if (args.Length > 0) int.TryParse(args[0], out port);

        using (HttpListener listener = new HttpListener())
        {
            listener.Prefixes.Add(string.Format("http://localhost:{0}/", port));
            listener.Start();
            Console.WriteLine("[ds-proxy] Listening on http://localhost:{0}", port);
            Console.WriteLine("[ds-proxy] Forwarding to https://{0}{1}", TARGET_HOST, TARGET_PATH);
            Console.WriteLine("[ds-proxy] Press Ctrl+C to stop");

            while (true)
            {
                HttpListenerContext ctx = listener.GetContext();
                HandleRequest(ctx);
            }
        }
    }

    static void SetSslProtocols()
    {
        try
        {
            // Enable TLS 1.2 for .NET 4.x
            System.Net.ServicePointManager.SecurityProtocol =
                (System.Net.SecurityProtocolType)3072  // TLS 1.2
                | (System.Net.SecurityProtocolType)768   // TLS 1.1
                | System.Net.SecurityProtocolType.Tls;
        }
        catch { }
    }

    static void HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            string requestBody = "";
            using (StreamReader reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            {
                requestBody = reader.ReadToEnd();
            }

            bool isStreaming = requestBody.Contains("\"stream\":true");

            // Inject thinking blocks from previous response
            string modifiedBody = InjectThinking(requestBody);

            string rawUrl = ctx.Request.RawUrl ?? "";
            string url = "https://" + TARGET_HOST + TARGET_PATH + rawUrl;

            HttpWebRequest forward = (HttpWebRequest)WebRequest.Create(url);
            forward.Method = ctx.Request.HttpMethod;
            forward.ContentType = ctx.Request.ContentType;
            forward.AllowAutoRedirect = true;

            if (isStreaming)
            {
                forward.AllowReadStreamBuffering = false;
            }

            // Copy headers
            foreach (string header in ctx.Request.Headers.AllKeys)
            {
                string lower = (header ?? "").ToLower();
                if (lower == "host" || lower == "content-length" || lower == "connection"
                    || lower == "transfer-encoding" || lower == "keep-alive")
                    continue;

                try
                {
                    string value = ctx.Request.Headers[header];
                    switch (lower)
                    {
                        case "content-type":
                            forward.ContentType = value;
                            break;
                        case "accept":
                            forward.Accept = value;
                            break;
                        case "user-agent":
                            forward.UserAgent = value;
                            break;
                        case "referer":
                            forward.Referer = value;
                            break;
                        default:
                            forward.Headers[header] = value;
                            break;
                    }
                }
                catch { /* skip restricted headers */ }
            }

            // Write request body
            if (!string.IsNullOrEmpty(modifiedBody))
            {
                byte[] bodyBytes = Encoding.UTF8.GetBytes(modifiedBody);
                forward.ContentLength = bodyBytes.Length;
                using (Stream stream = forward.GetRequestStream())
                {
                    stream.Write(bodyBytes, 0, bodyBytes.Length);
                }
            }

            // Forward and read response
            using (HttpWebResponse upstream = (HttpWebResponse)forward.GetResponse())
            {
                if (isStreaming)
                {
                    ProxyStreaming(ctx, upstream);
                }
                else
                {
                    ProxyJson(ctx, upstream);
                }
            }
        }
        catch (WebException wex)
        {
            if (wex.Response != null)
            {
                using (HttpWebResponse errResp = (HttpWebResponse)wex.Response)
                using (StreamReader reader = new StreamReader(errResp.GetResponseStream(), Encoding.UTF8))
                {
                    string errBody = reader.ReadToEnd();
                    ctx.Response.StatusCode = (int)errResp.StatusCode;
                    ctx.Response.ContentType = "application/json";
                    byte[] bytes = Encoding.UTF8.GetBytes(errBody);
                    ctx.Response.ContentLength64 = bytes.Length;
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                }
            }
            else
            {
                WriteError(ctx, 502, wex.Message);
            }
        }
        catch (Exception ex)
        {
            WriteError(ctx, 500, ex.Message);
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    static void ProxyJson(HttpListenerContext ctx, HttpWebResponse upstream)
    {
        string responseBody;
        using (StreamReader reader = new StreamReader(upstream.GetResponseStream(), Encoding.UTF8))
        {
            responseBody = reader.ReadToEnd();
        }

        // Handle empty response
        if (string.IsNullOrEmpty(responseBody))
        {
            ctx.Response.StatusCode = (int)upstream.StatusCode;
            ctx.Response.ContentType = "application/json";
            byte[] emptyBytes = new byte[0];
            ctx.Response.ContentLength64 = 0;
            ctx.Response.OutputStream.Write(emptyBytes, 0, 0);
            return;
        }

        ExtractThinkingFromJson(responseBody);

        ctx.Response.StatusCode = (int)upstream.StatusCode;
        ctx.Response.ContentType = upstream.ContentType ?? "application/json";

        byte[] bytes = Encoding.UTF8.GetBytes(responseBody);
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    static void ProxyStreaming(HttpListenerContext ctx, HttpWebResponse upstream)
    {
        ctx.Response.StatusCode = (int)upstream.StatusCode;
        ctx.Response.ContentType = upstream.ContentType ?? "text/event-stream";
        ctx.Response.SendChunked = true;

        lock (lockObj)
        {
            thinkingAccum.Clear();
        }

        bool sawMessageStop = false;

        using (Stream upstreamStream = upstream.GetResponseStream())
        using (Stream clientStream = ctx.Response.OutputStream)
        {
            byte[] buffer = new byte[8192];

            while (true)
            {
                int bytesRead;
                try
                {
                    bytesRead = upstreamStream.Read(buffer, 0, buffer.Length);
                }
                catch
                {
                    break;
                }
                if (bytesRead <= 0) break;

                try
                {
                    clientStream.Write(buffer, 0, bytesRead);
                    clientStream.Flush();
                }
                catch { break; }

                string chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                bool foundStop = ProcessStreamChunk(chunk);
                if (foundStop) sawMessageStop = true;
            }
        }

        if (sawMessageStop)
        {
            AssembleThinkingFromSse();
        }
    }

    static bool ProcessStreamChunk(string chunk)
    {
        bool foundStop = false;
        string[] lines = chunk.Split('\n');

        foreach (string rawLineTmp in lines)
        {
            string rawLine = rawLineTmp.TrimEnd('\r');
            if (string.IsNullOrEmpty(rawLine)) continue;

            if (rawLine.StartsWith("event:") && rawLine.IndexOf("message_stop") >= 0)
            {
                foundStop = true;
            }

            if (rawLine.StartsWith("data:"))
            {
                string data = rawLine.Substring(5).TrimStart();
                ParseSseData(data);
            }
        }

        return foundStop;
    }

    static void ParseSseData(string data)
    {
        try
        {
            Dictionary<string, object> obj = json.DeserializeObject(data) as Dictionary<string, object>;
            if (obj == null) return;

            // content_block_start: {"type":"content_block_start","index":N,"content_block":{"type":"thinking","thinking":""}}
            string type = GetString(obj, "type");
            if (type == "content_block_start")
            {
                Dictionary<string, object> block = obj["content_block"] as Dictionary<string, object>;
                if (block != null && GetString(block, "type") == "thinking")
                {
                    int index = Convert.ToInt32(obj["index"]);
                    lock (lockObj)
                    {
                        thinkingAccum[index] = new StringBuilder();
                    }
                }
            }
            // content_block_delta: {"type":"content_block_delta","index":N,"delta":{"type":"thinking_delta","thinking":"..."}}
            else if (type == "content_block_delta")
            {
                Dictionary<string, object> delta = obj["delta"] as Dictionary<string, object>;
                if (delta != null && GetString(delta, "type") == "thinking_delta")
                {
                    int index = Convert.ToInt32(obj["index"]);
                    string thinking = GetString(delta, "thinking");
                    lock (lockObj)
                    {
                        if (!thinkingAccum.ContainsKey(index))
                            thinkingAccum[index] = new StringBuilder();
                        thinkingAccum[index].Append(thinking);
                    }
                }
            }
        }
        catch { /* skip malformed SSE events */ }
    }

    static void AssembleThinkingFromSse()
    {
        lock (lockObj)
        {
            pendingThinkingBlocks.Clear();
            foreach (var kvp in thinkingAccum)
            {
                var block = new Dictionary<string, object>();
                block["type"] = "thinking";
                block["thinking"] = kvp.Value.ToString();
                pendingThinkingBlocks.Add(block);
                Console.WriteLine("[ds-proxy] Extracted thinking block ({0} chars) from SSE",
                    kvp.Value.Length);
            }
            thinkingAccum.Clear();
        }
    }

    static void ExtractThinkingFromJson(string responseBody)
    {
        try
        {
            Dictionary<string, object> response = json.DeserializeObject(responseBody) as Dictionary<string, object>;
            if (response == null || !response.ContainsKey("content")) return;

            ArrayList content = response["content"] as ArrayList;
            if (content == null) return;

            lock (lockObj)
            {
                pendingThinkingBlocks.Clear();
                foreach (object item in content)
                {
                    Dictionary<string, object> block = item as Dictionary<string, object>;
                    if (block != null && GetString(block, "type") == "thinking")
                    {
                        pendingThinkingBlocks.Add(new Dictionary<string, object>(block));
                        string thinking = GetString(block, "thinking");
                        Console.WriteLine("[ds-proxy] Extracted thinking block ({0} chars) from JSON",
                            (thinking ?? "").Length);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[ds-proxy] Error extracting thinking: {0}", ex.Message);
        }
    }

    static string InjectThinking(string requestBody)
    {
        if (string.IsNullOrEmpty(requestBody)) return requestBody;

        List<Dictionary<string, object>> blocksToInject = null;
        lock (lockObj)
        {
            if (pendingThinkingBlocks.Count > 0)
            {
                blocksToInject = new List<Dictionary<string, object>>(pendingThinkingBlocks);
                pendingThinkingBlocks.Clear();
            }
        }

        if (blocksToInject == null || blocksToInject.Count == 0) return requestBody;

        try
        {
            Dictionary<string, object> request = json.DeserializeObject(requestBody) as Dictionary<string, object>;
            if (request == null || !request.ContainsKey("messages")) return requestBody;

            ArrayList messages = request["messages"] as ArrayList;
            if (messages == null) return requestBody;

            // Find the last assistant message
            Dictionary<string, object> lastAssistant = null;
            foreach (object msgObj in messages)
            {
                Dictionary<string, object> msg = msgObj as Dictionary<string, object>;
                if (msg != null && msg.ContainsKey("role")
                    && GetString(msg, "role") == "assistant")
                {
                    lastAssistant = msg;
                }
            }

            if (lastAssistant == null) return requestBody;

            ArrayList contentArray = lastAssistant["content"] as ArrayList;
            if (contentArray == null) return requestBody;

            // Check if already has thinking blocks
            bool hasThinking = false;
            foreach (object blockObj in contentArray)
            {
                Dictionary<string, object> block = blockObj as Dictionary<string, object>;
                if (block != null && GetString(block, "type") == "thinking")
                {
                    hasThinking = true;
                    break;
                }
            }

            if (!hasThinking)
            {
                foreach (var block in blocksToInject)
                {
                    contentArray.Add(block);
                }
                Console.WriteLine("[ds-proxy] Injected {0} thinking block(s) into assistant message",
                    blocksToInject.Count);
            }

            return json.Serialize(request);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[ds-proxy] Error injecting thinking: {0}", ex.Message);
            // Put blocks back so we can retry next time
            lock (lockObj)
            {
                pendingThinkingBlocks.InsertRange(0, blocksToInject);
            }
            return requestBody;
        }
    }

    static string GetString(Dictionary<string, object> dict, string key)
    {
        if (dict == null || !dict.ContainsKey(key)) return "";
        object val = dict[key];
        return val != null ? val.ToString() : "";
    }

    static void WriteError(HttpListenerContext ctx, int statusCode, string message)
    {
        try
        {
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = "application/json";
            string body = string.Format("{{\"error\":{{\"type\":\"proxy_error\",\"message\":\"{0}\"}}}}",
                message.Replace("\"", "\\\""));
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        catch { }
    }
}
