namespace MachineVoice.Protocol;

public static class ProtocolVersion
{
    public const int Current = 1;
}

public static class ProtocolErrors
{
    public const string InvalidState = "invalid-state";
    public const string NotFound = "not-found";
    public const string InvalidArgument = "invalid-argument";
    public const string InvalidMessage = "invalid-message";
    public const string UnknownCommand = "unknown-command";
    public const string UnsupportedVersion = "unsupported-version";

    /// <summary>A service that the command needs (the speech server) did not work; see the detail.</summary>
    public const string Unavailable = "unavailable";
}
