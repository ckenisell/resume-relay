namespace ResumeRelay;

/// <summary>
/// getPlayStatus reports the player's internal mount path, e.g.
///   /data/system/smb/10.0.0.56#Entertainment/Movies/Braveheart/Braveheart (1995).4K.(1#Theatrical Movie).mkv
/// but getIdByPathname needs the source-URL spelling:
///   smb://10.0.0.56/Entertainment/Movies/Braveheart/Braveheart (1995).4K.(1#Theatrical Movie).mkv
/// The FIRST '#' after the server separates server from share; any later '#' belongs to the file name.
/// </summary>
public static class PathMapper
{
    private static readonly string[] Schemes = { "smb", "nfs" };

    public static string? ToSourceUrl(string? playPath)
    {
        if (string.IsNullOrWhiteSpace(playPath)) return null;
        string p = playPath.Trim().Replace('\\', '/');

        // Already a URL (some builds may report it that way).
        if (p.Contains("://")) return p;

        foreach (string scheme in Schemes)
        {
            int at = p.IndexOf("/" + scheme + "/", StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            string rest = p[(at + scheme.Length + 2)..];      // "10.0.0.56#Entertainment/Movies/..."
            int hash = rest.IndexOf('#');
            int slash = rest.IndexOf('/');
            if (hash <= 0 || (slash >= 0 && slash < hash)) continue;
            string host = rest[..hash];
            string remainder = rest[(hash + 1)..].TrimStart('/');
            if (remainder.Length == 0) continue;
            return $"{scheme}://{host}/{remainder}";
        }
        return null; // a USB/internal drive, or a layout we do not know: cannot be matched across players
    }

    /// <summary>Compares two source URLs the way the player would: case-insensitive, percent-decoded, slash-normalised.</summary>
    public static bool Same(string? a, string? b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? s)
    {
        string v = (s ?? "").Trim().Replace('\\', '/');
        try { v = Uri.UnescapeDataString(v); } catch { }
        return v.TrimEnd('/');
    }

    public static string FileName(string? path)
    {
        string p = (path ?? "").Replace('\\', '/').TrimEnd('/');
        int i = p.LastIndexOf('/');
        return i >= 0 ? p[(i + 1)..] : p;
    }
}
