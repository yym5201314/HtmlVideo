using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HtmlVideoLite
{
    // Chromium pushes composited frames over Page.startScreencast. Unlike
    // Page.captureScreenshot, this never asks the compositor to produce a new
    // frame, so a stalled or very heavy page cannot block the render loop - the
    // same reason Electron's offscreen paint events never time out.
    // The socket carries requests, responses and events, so it runs its own
    // receive loop instead of the request/response pattern in Cdp.
    internal sealed class Screencast : IDisposable
    {
        readonly ClientWebSocket socket = new ClientWebSocket();
        readonly CancellationToken token;
        readonly ConcurrentDictionary<int, TaskCompletionSource<Dictionary<string, object>>> pending =
            new ConcurrentDictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
        readonly SemaphoreSlim arrival = new SemaphoreSlim(0);
        readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);
        readonly object sync = new object();
        readonly byte[] buffer = new byte[262144];
        Task reader;
        int sequence;
        byte[] latest;
        bool closed, disposed;

        internal Screencast(CancellationToken token)
        {
            this.token = token;
            socket.Options.Proxy = null;
        }

        internal async Task Start(string endpoint, int width, int height, int quality)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(20000);
                await socket.ConnectAsync(new Uri(endpoint), timeout.Token).ConfigureAwait(false);
            }
            // The reader must be running before the first request is awaited: it is
            // what delivers responses as well as frames.
            reader = Task.Run(() => Receive());
            await Call("Page.enable", new { }).ConfigureAwait(false);
            await Call("Page.startScreencast", new {
                format = "jpeg", quality = quality, maxWidth = width, maxHeight = height, everyNthFrame = 1
            }).ConfigureAwait(false);
        }

        // Discard frames produced before the next clock step, so the frame the
        // caller picks up afterwards belongs to the step it is about to take.
        internal void Drain() { while (arrival.Wait(0)) { } }

        // Wait for one pushed frame and return the freshest image.
        internal async Task<byte[]> Next(int timeoutMs)
        {
            try
            {
                if (!await arrival.WaitAsync(timeoutMs, token).ConfigureAwait(false)) return null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return null; }
            // A dead socket must never hand back its last frame as if it were new.
            lock (sync) return closed ? null : latest;
        }

        async Task Receive()
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    using (var stream = new MemoryStream())
                    {
                        WebSocketReceiveResult received;
                        do
                        {
                            received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                            if (received.MessageType == WebSocketMessageType.Close) return;
                            stream.Write(buffer, 0, received.Count);
                        } while (!received.EndOfMessage);
                        Dispatch(stream.ToArray());
                    }
                }
            }
            catch (Exception) { }
            finally { Shutdown(); }
        }

        void Dispatch(byte[] payload)
        {
            Dictionary<string, object> message;
            try { message = Cdp.Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(payload)); }
            catch (Exception) { return; }
            if (message.ContainsKey("method"))
            {
                if (Convert.ToString(message["method"]) == "Page.screencastFrame")
                    Frame(message.ContainsKey("params") ? message["params"] as Dictionary<string, object> : null);
                return;
            }
            if (!message.ContainsKey("id")) return;
            int id;
            if (!int.TryParse(Convert.ToString(message["id"]), out id)) return;
            TaskCompletionSource<Dictionary<string, object>> waiter;
            if (!pending.TryRemove(id, out waiter)) return;
            if (message.ContainsKey("error"))
                waiter.SetException(new InvalidOperationException("截图流请求失败：" + Cdp.Json.Serialize(message["error"])));
            else waiter.SetResult(message.ContainsKey("result") ?
                message["result"] as Dictionary<string, object> : new Dictionary<string, object>());
        }

        void Frame(Dictionary<string, object> parameters)
        {
            if (parameters == null) return;
            try
            {
                // Decode immediately: the raw frame is only kept once and the base64
                // copy can be collected by the next GC.
                var data = Convert.FromBase64String(Convert.ToString(parameters["data"]));
                lock (sync) { if (closed) return; latest = data; }
                arrival.Release();
            }
            catch (Exception) { return; }
            int session;
            if (parameters.ContainsKey("sessionId") &&
                int.TryParse(Convert.ToString(parameters["sessionId"]), out session))
            {
                // Frames stop arriving until each one is acknowledged.
                var ack = Send("Page.screencastFrameAck", new { sessionId = session });
                ack.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        async Task<Dictionary<string, object>> Call(string method, object parameters)
        {
            int id = Interlocked.Increment(ref sequence);
            var waiter = new TaskCompletionSource<Dictionary<string, object>>();
            pending[id] = waiter;
            await Send(method, parameters, id).ConfigureAwait(false);
            var finished = await Task.WhenAny(waiter.Task, Task.Delay(20000)).ConfigureAwait(false);
            if (finished != waiter.Task)
            {
                TaskCompletionSource<Dictionary<string, object>> lost;
                pending.TryRemove(id, out lost);
                throw new TimeoutException("截图流请求超过 20 秒：" + method);
            }
            return await waiter.Task.ConfigureAwait(false);
        }        Task Send(string method, object parameters) { return Send(method, parameters, 0); }

        async Task Send(string method, object parameters, int id)
        {
            if (id == 0) id = Interlocked.Increment(ref sequence);
            byte[] payload = Encoding.UTF8.GetBytes(
                Cdp.Json.Serialize(new { id = id, method = method, @params = parameters }));
            await sendGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            }
            finally { sendGate.Release(); }
        }

        void Shutdown()
        {
            lock (sync) { if (closed) return; closed = true; }
            // Wake every waiter so the render loop falls back to normal capture.
            arrival.Release();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Shutdown();
            try { socket.Abort(); } catch (Exception) { }
            try { socket.Dispose(); } catch (Exception) { }
            if (reader != null) { try { reader.Wait(2000); } catch (AggregateException) { } }
        }
    }
}
