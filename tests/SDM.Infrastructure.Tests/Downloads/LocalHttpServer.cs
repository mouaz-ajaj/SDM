using System.Net;
using System.Net.Sockets;

namespace SDM.Infrastructure.Tests.Downloads;

/// <summary>
/// A loopback HTTP server bound to a free port. Engine tests are deterministic and
/// never reach the public internet.
/// </summary>
internal sealed class LocalHttpServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<HttpListenerContext, CancellationToken, Task> _handler;

    public LocalHttpServer(Func<HttpListenerContext, CancellationToken, Task> handler)
    {
        _handler = handler;
        BaseAddress = new Uri($"http://localhost:{FindFreePort()}/");

        _listener = new HttpListener();
        _listener.Prefixes.Add(BaseAddress.AbsoluteUri);
        _listener.Start();

        _ = Task.Run(AcceptLoopAsync);
    }

    public Uri BaseAddress { get; }

    public Uri Url(string relativePath) => new(BaseAddress, relativePath);

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                await _handler(context, _shutdown.Token);
            }
            catch (Exception)
            {
                // The client hanging up mid-response is exactly what the cancellation
                // test provokes, so a broken pipe here is expected rather than a failure.
            }
            finally
            {
                try
                {
                    context.Response.Close();
                }
                catch (Exception)
                {
                    // Already torn down by the client.
                }
            }
        }
    }

    private static int FindFreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _listener.Close();
        _shutdown.Dispose();
    }
}
