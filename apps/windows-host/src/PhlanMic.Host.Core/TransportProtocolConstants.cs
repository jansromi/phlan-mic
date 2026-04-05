namespace PhlanMic.Host.Core;

public static class TransportProtocolConstants
{
    public const int ProtocolVersion = 1;
    public const string AudioPacketMagic = "PHLM";
    public const int AudioPacketHeaderSize = 44;
    public const int MaxPayloadLengthBytes = 8 * 1024;
}
