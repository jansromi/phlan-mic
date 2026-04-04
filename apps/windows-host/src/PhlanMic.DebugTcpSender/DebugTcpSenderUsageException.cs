namespace PhlanMic.DebugTcpSender;

internal sealed class DebugTcpSenderUsageException : Exception
{
    public DebugTcpSenderUsageException(string message)
        : base(message)
    {
    }
}
