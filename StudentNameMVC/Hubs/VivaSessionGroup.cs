namespace StudentNameMVC.Hubs;

public static class VivaSessionGroup
{
    public static string Name(Guid sessionId)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Session ID is required.", nameof(sessionId));
        return $"viva-session-{sessionId:D}";
    }
}
