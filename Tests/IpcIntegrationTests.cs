using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ShokoMpcWindows;

class IpcIntegrationTests
{
    sealed class Endpoint : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            if (request.Method != HttpMethod.Post || request.RequestUri.AbsolutePath != "/api/plugin/ShokoMpc/complete/" + new string('b', 64) + "/0")
                throw new Exception("Unexpected completion request");
            Calls++;
            await Task.Delay(50, cancellation);
            return new HttpResponseMessage(Calls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent("{\"watched\":true}") };
        }
    }
    [STAThread] static int Main(string[] args)
    {
        var endpoint = new Endpoint();
        using (var client = new HttpClient(endpoint))
        using (var monitor = new PlaybackMonitor(args[0], new[] { "http://localhost/api/plugin/ShokoMpc/stream/" + new string('a', 64) + "/Episode.mkv" }, "http://localhost/api/plugin/ShokoMpc/complete/" + new string('b', 64), client))
            Application.Run(monitor);
        if (endpoint.Calls != 2) throw new Exception("Expected a completion and one retry after native IPC progress, received " + endpoint.Calls);
        Console.WriteLine("PASS: real cross-process MPC IPC handshake, polling, 90% completion, asynchronous retry and monitor shutdown");
        return 0;
    }
}
