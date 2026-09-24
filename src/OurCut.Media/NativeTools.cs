namespace OurCut.Media;

/// <summary>
/// Locates the native tools OurCut depends on. Lookup order: an explicit directory
/// (settings or OURCUT_FFMPEG_DIR), then the app directory (filled by scripts/fetch-deps.ps1),
/// then PATH.
/// </summary>
public static class NativeTools
{
    public const string FfmpegDirEnvironmentVariable = "OURCUT_FFMPEG_DIR";

    public static string ExecutableName(string tool) =>
        OperatingSystem.IsWindows() ? tool + ".exe" : tool;

    /// <summary>Returns the full path of <paramref name="tool"/> (e.g. "ffprobe"), or null if not found.</summary>
    public static string? FindTool(string tool, string? preferredDirectory = null)
    {
        string file = ExecutableName(tool);
        foreach (string? dir in CandidateDirectories(preferredDirectory))
        {
            if (string.IsNullOrEmpty(dir))
                continue;
            string path = Path.Combine(dir, file);
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    private static IEnumerable<string?> CandidateDirectories(string? preferredDirectory)
    {
        yield return preferredDirectory;
        yield return Environment.GetEnvironmentVariable(FfmpegDirEnvironmentVariable);
        yield return AppContext.BaseDirectory;
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is null)
            yield break;
        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return dir;
    }
}
