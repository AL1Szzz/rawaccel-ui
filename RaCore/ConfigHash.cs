using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using RaCore.Model;

namespace RaCore;

/// <summary>
/// Stable short hashes of driver-visible state. The profile hash is taken over the exact bytes
/// handed to the driver, so the same 12 characters appear on the plan list, in the tray tooltip
/// and in the score history - it is the thing that proves the curve you saved is the curve running.
/// </summary>
public static class ConfigHash
{
    /// <summary>Short stable hash of one profile exactly as it would be handed to the driver.</summary>
    public static string Of(ProfileDto p)
    {
        var s = Convert4.ToStruct(p);
        var span = MemoryMarshal.CreateReadOnlySpan(ref s, 1);
        var bytes = MemoryMarshal.AsBytes(span).ToArray();
        return Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
    }

    /// <summary>Hash over the whole document (profiles + devices + defaults), for drift checks.</summary>
    public static string OfDoc(RootDto r)
    {
        var bytes = Encoding.UTF8.GetBytes(Json.Serialize(r));
        return Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
    }
}
