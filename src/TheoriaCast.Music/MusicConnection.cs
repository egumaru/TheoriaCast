namespace TheoriaCast.Music;

public sealed record MusicConnection(int schema_version, string service, string server_id, string name, string api_url,
    string mcp_url, string token)
{
    public static MusicConnection Create(string address, string name, string id, string token)
    {
        var api = BaseUri(address);
        return new(1, "TheoriaCast.Music.Api", id, string.IsNullOrWhiteSpace(name) ? api.Authority : name.Trim(),
            api.AbsoluteUri.TrimEnd('/'), new Uri(api, "mcp").AbsoluteUri, token);
    }

    public MusicConnection Validate()
    {
        if (schema_version != 1 || service != "TheoriaCast.Music.Api" || string.IsNullOrWhiteSpace(name) || name.Length > 200)
            throw new ArgumentException("TheoriaCast.Music.Api の接続ファイルを選んでください。");
        if (!Guid.TryParseExact(server_id, "N", out _) || string.IsNullOrWhiteSpace(token) ||
            token.Length is < 32 or > 256 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("接続ファイルの識別子またはトークンが無効です。");
        var normalized = Create(api_url, name, server_id, token);
        if (!Uri.TryCreate(mcp_url, UriKind.Absolute, out var mcp) || normalized.mcp_url != mcp.AbsoluteUri)
            throw new ArgumentException("MCP の接続先が API の設定と一致しません。");
        return normalized;
    }

    public static Uri BaseUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Host is "0.0.0.0" or "[::]" or "*" or "+")
            throw new ArgumentException("クライアントから到達できる HTTP または HTTPS の URL を指定してください。");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }
}
