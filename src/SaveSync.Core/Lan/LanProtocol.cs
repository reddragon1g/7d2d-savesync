using System.Net.Sockets;
using System.Text;

namespace SaveSync.Core.Lan;

/// <summary>
/// Wire format for PC-to-PC transfers: a 4-byte big-endian length, a UTF-8 JSON header, then an
/// optional raw body of the length the header declares.
///
/// Deliberately a framed protocol over a plain TcpListener rather than HttpListener: binding a
/// non-localhost HTTP prefix needs an admin URL reservation, and this has to run as an ordinary
/// user on someone else's PC.
/// </summary>
public static class LanProtocol
{
    public const int DefaultPort = 47365;
    public const int DiscoveryPort = 47366;

    /// <summary>Bumped only for breaking changes; both ends refuse a version they do not know.</summary>
    public const int Version = 1;

    /// <summary>Guards against a hostile or confused peer claiming an enormous header.</summary>
    public const int MaxHeaderBytes = 1 << 20;

    /// <summary>Largest single file accepted in one transfer. Region files are far below this.</summary>
    public const long MaxBodyBytes = 8L * 1024 * 1024 * 1024;

    public static async Task WriteMessageAsync(
        Stream stream, object header, Stream? body = null, long bodyLength = 0, CancellationToken ct = default)
    {
        var json = Encoding.UTF8.GetBytes(Json.Write(header));
        var len = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, json.Length);

        await stream.WriteAsync(len, ct).ConfigureAwait(false);
        await stream.WriteAsync(json, ct).ConfigureAwait(false);

        if (body is not null && bodyLength > 0)
        {
            await CopyExactAsync(body, stream, bodyLength, ct).ConfigureAwait(false);
        }

        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T?> ReadHeaderAsync<T>(Stream stream, CancellationToken ct = default)
    {
        var len = new byte[4];
        if (!await ReadExactAsync(stream, len, 4, ct).ConfigureAwait(false)) return default;

        int size = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(len);
        if (size <= 0 || size > MaxHeaderBytes)
            throw new InvalidDataException($"Bad message size ({size}).");

        var buffer = new byte[size];
        if (!await ReadExactAsync(stream, buffer, size, ct).ConfigureAwait(false))
            throw new EndOfStreamException("Connection closed part way through a message.");

        return Json.Read<T>(Encoding.UTF8.GetString(buffer));
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes, or returns false at a clean EOF.</summary>
    public static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n == 0) return read == 0 ? false : throw new EndOfStreamException("Connection closed mid-message.");
            read += n;
        }
        return true;
    }

    /// <summary>
    /// Copies exactly <paramref name="length"/> bytes. A short read is an error rather than a
    /// truncated file, because a truncated save is exactly what this tool exists to prevent.
    /// </summary>
    public static async Task CopyExactAsync(Stream from, Stream to, long length, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long remaining = length;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int n = await from.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("Stream ended before all bytes arrived.");
            await to.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            remaining -= n;
        }
    }

    public static void Configure(TcpClient client)
    {
        client.NoDelay = true;
        client.SendTimeout = 60_000;
        client.ReceiveTimeout = 60_000;
        client.SendBufferSize = 1 << 20;
        client.ReceiveBufferSize = 1 << 20;
    }
}

// ---------------------------------------------------------------------- messages

public sealed class LanRequest
{
    public int Version { get; set; } = LanProtocol.Version;

    /// <summary>hello | pair | list-saves | request-send | push-begin | push-file | push-commit | push-abort</summary>
    public string Op { get; set; } = "";

    /// <summary>Shared secret agreed at pairing. Absent for hello and pair.</summary>
    public string? Secret { get; set; }

    public string MachineId { get; set; } = "";
    public string DisplayName { get; set; } = "";

    /// <summary>Who is sending, as a person's name, for the prompt the receiver shows.</summary>
    public string SenderName { get; set; } = "";

    public string? Token { get; set; }

    /// <summary>Where to send things back to, so the far side can start a transfer of its own.</summary>
    public int ReplyPort { get; set; }

    // push-begin
    public string? SaveId { get; set; }
    public string? SaveLabel { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }

    // push-file
    public string? Path { get; set; }
    public long BodyBytes { get; set; }
    public string? Sha256 { get; set; }

    /// <summary>Which part of the package this file belongs to: payload or world.</summary>
    public string? Section { get; set; }

    /// <summary>
    /// The file's last-written time, so the far side can restore it exactly.
    ///
    /// Writing a received file gives it a brand new timestamp, which made every save that arrived
    /// over the network look like it had just been played and produced conflicts out of nowhere.
    /// A stick copy preserves these, so the network has to as well.
    /// </summary>
    public long MTimeTicks { get; set; }

    // push-commit
    public string? PackageInfoJson { get; set; }
    public string? ManifestJson { get; set; }
}

public sealed class LanResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }

    public int Version { get; set; } = LanProtocol.Version;
    public string MachineId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ToolVersion { get; set; } = "";

    /// <summary>True when this peer already trusts the caller.</summary>
    public bool Paired { get; set; }

    /// <summary>Returned by a successful pair.</summary>
    public string? Secret { get; set; }

    public string? Token { get; set; }

    /// <summary>Serialised list of PeerSave, for list-saves.</summary>
    public string? SavesJson { get; set; }

    // push-commit results
    public bool Applied { get; set; }
    public string? Relation { get; set; }
    public string? Message { get; set; }

    public static LanResponse Fail(string error) => new() { Ok = false, Error = error };
}

/// <summary>One save as the other PC describes it, with enough history to compare against ours.</summary>
public sealed class PeerSave
{
    public string SaveId { get; set; } = "";
    public string World { get; set; } = "";
    public string SaveName { get; set; } = "";

    /// <summary>Null when that PC has a save it has never copied anywhere.</summary>
    public Passport? Passport { get; set; }

    public long SizeBytes { get; set; }
    public DateTimeOffset LastPlayedAt { get; set; }

    /// <summary>That PC has played since it last packaged this save, so its passport understates it.</summary>
    public bool PlayedSinceLastCopy { get; set; }

    public string Display => $"{SaveName} ({World})";
}

/// <summary>What a machine shouts on the LAN so the other one can find it without typing an address.</summary>
public sealed class Beacon
{
    public int Version { get; set; } = LanProtocol.Version;
    public string MachineId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PersonName { get; set; } = "";
    public int Port { get; set; } = LanProtocol.DefaultPort;
}
