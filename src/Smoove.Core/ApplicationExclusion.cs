namespace Smoove.Core;

public sealed record ApplicationExclusion(string Path, bool Enabled = true, bool LegacyName = false)
{
    public static string Normalize(string path)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || !string.Equals(System.IO.Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Нужен полный путь к EXE.", nameof(path));
        return System.IO.Path.GetFullPath(path);
    }
    public bool Matches(string executable) => Enabled && (LegacyName
        ? string.Equals(System.IO.Path.GetFileNameWithoutExtension(Path), System.IO.Path.GetFileNameWithoutExtension(executable), StringComparison.OrdinalIgnoreCase)
        : string.Equals(Path, Normalize(executable), StringComparison.OrdinalIgnoreCase));
    public static ApplicationExclusion[] Migrate(string? names) => (names ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase).Select(n => new ApplicationExclusion(n, true, true)).ToArray();
}
