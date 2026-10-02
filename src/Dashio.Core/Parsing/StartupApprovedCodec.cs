namespace Dashio.Core.Parsing;

/// <summary>
/// The on/off flag Task Manager stores under <c>Explorer\StartupApproved</c>:
/// 12 bytes, first byte even = enabled, odd = disabled, bytes 4–11 = the time it was disabled.
/// </summary>
public static class StartupApprovedCodec
{
    public static bool IsEnabled(byte[]? value) =>
        value is null || value.Length == 0 || (value[0] & 1) == 0;

    public static byte[] Encode(bool enabled, DateTime utcNow)
    {
        var bytes = new byte[12];
        bytes[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled)
            BitConverter.GetBytes(utcNow.ToFileTimeUtc()).CopyTo(bytes, 4);
        return bytes;
    }
}
