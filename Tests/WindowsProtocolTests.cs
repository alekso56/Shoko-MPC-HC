using System;
using ShokoMpcWindows;
public class WindowsProtocolTests
{
    static int passed;
    static void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; }
    static void Reject(string link, string server) { try { Protocol.ParseTicket(link, server); } catch (ArgumentException) { passed++; return; } throw new Exception("Invalid link accepted"); }
    public static void Main()
    {
        var server = "http://localhost:30222";
        var ticket = new string('a', 64);
        var prefix = "shoko-mpc://play/?server=" + Uri.EscapeDataString(server);
        var link = prefix + "&ticket=" + ticket;
        Check(Protocol.ParseTicket(link, server) == ticket, "valid ticket");
        Reject(link, "http://other-server:30222"); Reject(link, "https://localhost:30222");
        Reject(link, "http://localhost:8111"); Reject(link, server + "/prefix");
        Reject(prefix + "&ticket=x", server); Reject(link + "&ticket=" + ticket, server);
        Reject(link + "&run=cmd.exe", server); Reject(link + "#fragment", server);
        Reject(link.Replace("play/", "play:123/"), server); Reject(link.Replace("play/", "play/other"), server);
        Reject(link.Replace("shoko-mpc:", "https:"), server); Reject(link + "\" /help", server);
        Check(Protocol.MediaUrl(server, "/api/plugin/ShokoMpc/stream/" + ticket) == server + "/api/plugin/ShokoMpc/stream/" + ticket, "same origin stream URL");
        foreach (var path in new[] { "https://evil.test/", "/api/plugin/ShokoMpc/stream/../../x", "\\\\host\\file", "/api/plugin/ShokoMpc/stream/" + ticket + "?command=1" })
        { try { Protocol.MediaUrl(server, path); } catch (ArgumentException) { passed++; continue; } throw new Exception("Invalid media path accepted"); }
        Check(Protocol.Quote("http://localhost/a") == "\"http://localhost/a\"", "player URL quoted");
        Console.WriteLine(passed + " Windows protocol checks passed.");
    }
}
