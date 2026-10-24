using System.Security.Cryptography;

namespace ShokoMpc;

public sealed class CompletionSession(PlaybackGrant playback)
{
    public PlaybackGrant Playback { get; } = playback;
    public bool[] Finished { get; } = new bool[playback.FileIds.Length];
    public HashSet<int> Saved { get; } = [];
    public long[] Sequences { get; } = new long[playback.FileIds.Length];
}

// Separate write capabilities: a media stream token cannot change watched status.
public sealed class CompletionSessions
{
    private readonly Dictionary<string, CompletionSession> sessions = new();
    private readonly object gate = new();
    private readonly Dictionary<(int User, int File), CompletionSession> current = new();
    // Serialize bookmark reads and writes, including a newer launch taking over a file.
    public SemaphoreSlim ProgressGate { get; } = new(1, 1);
    private readonly TimeProvider clock;
    public CompletionSessions() : this(TimeProvider.System) { }
    public CompletionSessions(TimeProvider clock) => this.clock = clock;
    public string Issue(int userId, int[] fileIds)
    {
        lock (gate)
        {
            Prune();
            if (sessions.Count >= 10000) throw new InvalidOperationException("Playback session limit reached.");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var session = new CompletionSession(new(userId, fileIds.ToArray(), clock.GetUtcNow().AddHours(12)));
            sessions.Add(token, session);
            foreach (var file in fileIds) current[(userId, file)] = session;
            return token;
        }
    }
    public CompletionSession? Find(string token)
    {
        lock (gate)
        {
            Prune();
            return PlaybackTokens.ValidToken(token) && sessions.TryGetValue(token, out var session) ? session : null;
        }
    }
    public bool IsCurrent(CompletionSession session)
    {
        lock (gate) return session.Playback.FileIds.All(file =>
            current.TryGetValue((session.Playback.UserId, file), out var active) && ReferenceEquals(active, session));
    }
    private void Prune()
    {
        foreach (var key in sessions.Where(x => x.Value.Playback.ExpiresAt <= clock.GetUtcNow()).Select(x => x.Key).ToArray())
        {
            var session = sessions[key];
            foreach (var file in session.Playback.FileIds)
                if (current.TryGetValue((session.Playback.UserId, file), out var active) && ReferenceEquals(active, session))
                    current.Remove((session.Playback.UserId, file));
            sessions.Remove(key);
        }
    }
}
