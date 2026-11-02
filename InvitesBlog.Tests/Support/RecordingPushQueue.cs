using InvitesBlog.Application.Push;

namespace InvitesBlog.Tests.Support;

/// <summary>Keeps every push job a service raised, so a test can say what was (or wasn't) queued.</summary>
public sealed class RecordingPushQueue : IPushQueue
{
    public System.Collections.Concurrent.ConcurrentQueue<PushJob> Jobs { get; } = new();
    public void Enqueue(PushJob job) => Jobs.Enqueue(job);
}
