namespace DeveLiveStreamer.Streaming;

public abstract record StreamEndpoint(string Host, int Port)
{
    /// <summary>
    /// Parses srt://, rtmp:// and rtmps:// URLs. A separate stream key is appended to RTMP paths
    /// and becomes the SRT stream ID when the URL has none.
    /// </summary>
    public static StreamEndpoint Parse(string url, string? streamKey = null)
    {
        url = url.Trim();
        streamKey = string.IsNullOrWhiteSpace(streamKey) ? null : streamKey.Trim();
        if (url.Length == 0) throw new FormatException("Enter a server URL, for example srt://server:9999?streamid=default/app/name.");
        var scheme = url.Split("://", 2)[0].ToLowerInvariant();
        return scheme switch
        {
            "srt" => SrtEndpoint.ParseUrl(url, streamKey),
            "rtmp" or "rtmps" => RtmpEndpoint.ParseUrl(url, streamKey),
            _ => throw new FormatException("Use a URL starting with srt://, rtmp:// or rtmps://.")
        };
    }

    internal static (string Host, int Port, string Remainder) SplitAuthority(string url, int defaultPort)
    {
        var afterScheme = url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var end = afterScheme.IndexOfAny(['/', '?']);
        var authority = end < 0 ? afterScheme : afterScheme[..end];
        var rest = end < 0 ? "" : afterScheme[end..];
        string host; var port = defaultPort;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0) throw new FormatException("The IPv6 address is missing its closing bracket.");
            host = authority[1..close];
            if (close + 1 < authority.Length && authority[close + 1] == ':') port = ParsePort(authority[(close + 2)..]);
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            host = colon < 0 ? authority : authority[..colon];
            if (colon >= 0) port = ParsePort(authority[(colon + 1)..]);
        }
        if (string.IsNullOrWhiteSpace(host)) throw new FormatException("The URL has no server address.");
        return (host, port, rest);
    }

    private static int ParsePort(string text) => int.TryParse(text, out var port) && port is > 0 and < 65536
        ? port : throw new FormatException($"'{text}' is not a valid port.");
}

public sealed record SrtEndpoint(string Host, int Port, string? StreamId, string? Passphrase, int KeyLength, bool Listener)
    : StreamEndpoint(Host, Port)
{
    internal static SrtEndpoint ParseUrl(string url, string? streamKey)
    {
        var (host, port, rest) = SplitAuthority(url, 0);
        if (port == 0) throw new FormatException("SRT URLs need a port, for example srt://server:9999.");
        var query = rest.Contains('?') ? rest[(rest.IndexOf('?') + 1)..] : "";
        string? streamId = null, passphrase = null; var keyLength = 0; var listener = false;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = (separator < 0 ? pair : pair[..separator]).ToLowerInvariant();
            var value = separator < 0 ? "" : Uri.UnescapeDataString(pair[(separator + 1)..]);
            switch (key)
            {
                case "streamid": streamId = value; break;
                case "passphrase": passphrase = value; break;
                case "pbkeylen": keyLength = int.TryParse(value, out var length) && length is 16 or 24 or 32 ? length : throw new FormatException("pbkeylen must be 16, 24 or 32."); break;
                case "mode": listener = value.Equals("listener", StringComparison.OrdinalIgnoreCase) || value.Equals("server", StringComparison.OrdinalIgnoreCase); break;
            }
        }
        if (passphrase is not null && passphrase.Length is < 10 or > 79) throw new FormatException("An SRT passphrase must be 10 to 79 characters.");
        if (streamId is not null && streamId.Length > 512) throw new FormatException("An SRT stream ID can be at most 512 characters.");
        if (host is "0.0.0.0" or "::" || host == "*") listener = true;
        return new(host, port, string.IsNullOrEmpty(streamId) ? streamKey : streamId, passphrase, keyLength, listener);
    }
}

public sealed record RtmpEndpoint(string Host, int Port, bool Tls, string App, string StreamName) : StreamEndpoint(Host, Port)
{
    public string TcUrl => $"{(Tls ? "rtmps" : "rtmp")}://{(Host.Contains(':') ? $"[{Host}]" : Host)}:{Port}/{App}";

    internal static RtmpEndpoint ParseUrl(string url, string? streamKey)
    {
        var tls = url.StartsWith("rtmps", StringComparison.OrdinalIgnoreCase);
        var (host, port, rest) = SplitAuthority(url, tls ? 443 : 1935);
        var path = rest.TrimStart('/');
        string app, stream;
        if (streamKey is not null)
        {
            app = path.TrimEnd('/');
            stream = streamKey;
        }
        else
        {
            // rtmp://host/app/stream: the final path segment (with any query) is the stream name.
            var query = path.IndexOf('?');
            var pathOnly = query < 0 ? path : path[..query];
            var slash = pathOnly.TrimEnd('/').LastIndexOf('/');
            if (slash < 0) throw new FormatException("Add a stream key, or use a URL like rtmp://server/app/stream.");
            app = path[..slash];
            stream = path[(slash + 1)..].TrimEnd('/');
        }
        if (app.Length == 0) throw new FormatException("The RTMP URL needs an application name, for example rtmp://server/app.");
        if (stream.Length == 0) throw new FormatException("The RTMP stream name or key is empty.");
        return new(host, port, tls, app, stream);
    }
}
