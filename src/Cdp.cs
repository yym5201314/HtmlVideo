using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace HtmlVideoLite
{
    internal sealed class Cdp : IDisposable
    {
        internal static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        readonly ClientWebSocket socket = new ClientWebSocket();
        readonly byte[] buffer = new byte[65536];
        readonly CancellationToken cancel;
        int sequence;

        internal Cdp(CancellationToken cancel) { this.cancel = cancel; socket.Options.Proxy = null; }

        internal async Task Connect(string url)
        {
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                {
                    timeout.CancelAfter(20000);
                    await socket.ConnectAsync(new Uri(url), timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                if (cancel.IsCancellationRequested) throw;
                throw new TimeoutException("连接浏览器调试端口超过 20 秒。");
            }
        }

        internal async Task<Dictionary<string, object>> Call(string method, object parameters, int timeoutMs = 60000)
        {
            try { return await Send(method, parameters, timeoutMs).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                if (cancel.IsCancellationRequested) throw;
                throw new TimeoutException("浏览器操作超过 " + timeoutMs / 1000 + " 秒：" + method);
            }
        }

        async Task<Dictionary<string, object>> Send(string method, object parameters, int timeoutMs)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                timeout.CancelAfter(timeoutMs);
                int id = ++sequence;
                byte[] request = Encoding.UTF8.GetBytes(Json.Serialize(new { id = id, method = method, @params = parameters }));
                await socket.SendAsync(new ArraySegment<byte>(request), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
                for (;;)
                {
                    using (var stream = new MemoryStream())
                    {
                        WebSocketReceiveResult received;
                        do
                        {
                            received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token).ConfigureAwait(false);
                            if (received.MessageType == WebSocketMessageType.Close)
                                throw new IOException("浏览器调试连接已关闭。");
                            stream.Write(buffer, 0, received.Count);
                        } while (!received.EndOfMessage);
                        var message = Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(stream.ToArray()));
                        if (!message.ContainsKey("id") || Convert.ToInt32(message["id"]) != id) continue;
                        if (message.ContainsKey("error"))
                            throw new InvalidOperationException(method + ": " + Json.Serialize(message["error"]));
                        return (Dictionary<string, object>)message["result"];
                    }
                }
            }
        }

        internal async Task<object> Evaluate(string expression, int timeoutMs = 60000)
        {
            var result = await Call("Runtime.evaluate", new {
                expression = expression, returnByValue = true, awaitPromise = true, userGesture = true
            }, timeoutMs).ConfigureAwait(false);
            if (result.ContainsKey("exceptionDetails"))
            {
                var details = (Dictionary<string, object>)result["exceptionDetails"];
                string description = Convert.ToString(details["text"]);
                if (details.ContainsKey("exception"))
                {
                    var exception = (Dictionary<string, object>)details["exception"];
                    if (exception.ContainsKey("description")) description = Convert.ToString(exception["description"]);
                }
                throw new InvalidOperationException("页面脚本执行失败：" + description.Split('\n')[0]);
            }
            var value = (Dictionary<string, object>)result["result"];
            return value.ContainsKey("value") ? value["value"] : null;
        }

        public void Dispose() { socket.Abort(); socket.Dispose(); }
    }
}
