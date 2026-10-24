using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
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
    sealed class ProgressEndpoint : HttpMessageHandler
    {
        public readonly List<double> Positions = new List<double>();
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            if (request.Method != HttpMethod.Post || request.RequestUri.AbsolutePath != "/api/plugin/ShokoMpc/progress/" + new string('b', 64) + "/0")
                throw new Exception("Unexpected progress request");
            var body = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(await request.Content.ReadAsStringAsync());
            var position = Convert.ToDouble(body["seconds"]);
            if (position == 0) throw new Exception("Initial or stopped zero overwrote the resume bookmark");
            Positions.Add(position); Calls++;
            await Task.Delay(50, cancellation);
            return new HttpResponseMessage(Calls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
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
        foreach (var filename in new[] { "Resume.mkv", "Finish.mkv" })
        {
            var progressEndpoint = new ProgressEndpoint();
            using (var client = new HttpClient(progressEndpoint))
            using (var monitor = new PlaybackMonitor(args[0], new[] { "http://localhost/api/plugin/ShokoMpc/stream/" + new string('a', 64) + "/" + filename },
                "http://localhost/api/plugin/ShokoMpc/complete/" + new string('b', 64), client,
                "http://localhost/api/plugin/ShokoMpc/progress/" + new string('b', 64), new[] { 35.5 })) Application.Run(monitor);
            if (progressEndpoint.Positions.First() != 35.5 || progressEndpoint.Positions.Last() != (filename == "Resume.mkv" ? 40 : 90) || progressEndpoint.Calls < 2)
                throw new Exception("Resume, final position, completion or retry was lost");
            Console.WriteLine("PASS: native seek restores bookmark once, retry preserves newest progress, stop/close flush and completion: " + filename);
        }
        return 0;
    }
}
