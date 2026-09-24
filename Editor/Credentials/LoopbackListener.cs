using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace com.amari_noa.amari_unity_package_registry_manager.editor
{
    internal sealed class LoopbackListener : IDisposable
    {
        private readonly TcpListener listener;
        private readonly LoopbackAddress address;
        private readonly string state;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly TaskCompletionSource<string> completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object gate = new object();
        private readonly HashSet<TcpClient> clients = new HashSet<TcpClient>();
        private readonly CancellationTokenRegistration cancellation;
        private readonly Task pump;
        private int responseClaimed;
        internal readonly string Redirect;
        internal LoopbackListener(LoopbackAddress address, bool dynamicPort, string state, CancellationToken cancel)
        {
            this.address = address; this.state = state;
            listener = new TcpListener(IPAddress.Loopback, dynamicPort ? 0 : address.Port);
            listener.Server.ExclusiveAddressUse = true;
            try { listener.Start(8); }
            catch (SocketException) { listener.Stop(); lifetime.Dispose(); throw new RegistryException(dynamicPort ? "callback_bind_failed" : "callback_port_busy"); }
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Redirect = dynamicPort ? address.WithPort(port) : address.Original;
            cancellation = cancel.Register(() => completion.TrySetCanceled());
            pump = Accept();
        }
        internal Task<string> Wait() { return completion.Task; }
        private async Task Accept()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    bool accepted;
                    lock (gate) { accepted = clients.Count < 4 && !lifetime.IsCancellationRequested; if (accepted) clients.Add(client); }
                    if (!accepted) { client.Close(); continue; }
                    _ = Handle(client);
                }
            }
            catch (Exception) { if (!lifetime.IsCancellationRequested) completion.TrySetException(new RegistryException("callback_listener_failed")); }
        }
        private async Task Handle(TcpClient client)
        {
            using (client)
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                using (deadline.Token.Register(() => client.Close()))
                {
                    try
                    {
                        var stream = client.GetStream(); var input = new List<byte>(); var chunk = new byte[1];
                        while (input.Count < 8192)
                        {
                            int count = await stream.ReadAsync(chunk, 0, 1, deadline.Token).ConfigureAwait(false);
                            if (count == 0) return; input.Add(chunk[0]);
                            int n = input.Count;
                            if (n >= 4 && input[n - 4] == 13 && input[n - 3] == 10 && input[n - 2] == 13 && input[n - 1] == 10) break;
                        }
                        string request = Encoding.ASCII.GetString(input.ToArray());
                        if (!request.EndsWith("\r\n\r\n", StringComparison.Ordinal)) return;
                        string[] lines = request.Split(new[] { "\r\n" }, StringSplitOptions.None);
                        string[] first = lines[0].Split(' ');
                        Dictionary<string, string> parameters = null;
                        if (first.Length == 3 && first[0] == "GET" && (first[2] == "HTTP/1.1" || first[2] == "HTTP/1.0") && first[1].Length <= 4096)
                            parameters = address.Parameters(first[1], state);
                        bool accepted = parameters != null && !completion.Task.IsCompleted && Interlocked.CompareExchange(ref responseClaimed, 1, 0) == 0;
                        bool succeeded = accepted && parameters.ContainsKey("code");
                        string body = !accepted ? "This request does not match an active sign-in. Return to Unity." : succeeded
                            ? "Authentication response received. You may close this window and return to Unity."
                            : "Sign-in was denied. Return to Unity to try again. No credentials were replaced.";
                        byte[] bytes = Encoding.ASCII.GetBytes("HTTP/1.1 " + (succeeded ? "200 OK" : "400 Bad Request") + "\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nPragma: no-cache\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'\r\nConnection: close\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\n\r\n" + body);
                        try { await stream.WriteAsync(bytes, 0, bytes.Length, deadline.Token).ConfigureAwait(false); }
                        finally
                        {
                            if (accepted)
                            {
                                if (parameters.ContainsKey("code")) completion.TrySetResult(parameters["code"]);
                                else completion.TrySetException(new RegistryException("oauth_consent_denied"));
                            }
                        }
                    }
                    catch (Exception) { }
                    finally { lock (gate) clients.Remove(client); }
                }
            }
        }
        public void Dispose()
        {
            cancellation.Dispose(); lifetime.Cancel(); listener.Stop();
            lock (gate) foreach (var client in clients) client.Close();
            completion.TrySetCanceled();
        }
    }
}
