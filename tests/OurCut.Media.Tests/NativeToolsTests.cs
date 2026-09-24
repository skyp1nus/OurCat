namespace OurCut.Media.Tests;

public class NativeToolsTests
{
    [Fact]
    public void FindTool_prefers_the_explicit_directory()
    {
        string dir = Directory.CreateTempSubdirectory("ourcut-tools").FullName;
        try
        {
            string exe = Path.Combine(dir, NativeTools.ExecutableName("ffprobe"));
            File.WriteAllText(exe, "");
            Assert.Equal(exe, NativeTools.FindTool("ffprobe", dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FindTool_returns_null_for_unknown_tool() =>
        Assert.Null(NativeTools.FindTool("ourcut-no-such-tool-" + Guid.NewGuid().ToString("N")));
}

public class NativeToolsVersionTests
{
    [Theory]
    [InlineData("ffmpeg version 7.1 Copyright (c) 2000-2024 the FFmpeg developers\nbuilt with gcc", "7.1")]
    [InlineData("ffmpeg version 6.1.1-3ubuntu5 Copyright (c) 2000-2023", "6.1.1")]
    [InlineData("ffmpeg version n9.0.1-11-ge47273f4d9-20260831 Copyright", "9.0.1")]
    [InlineData("ffprobe version 9.0.2-essentials_build-www.gyan.dev Copyright", "9.0.2")]
    [InlineData("ffmpeg version N-121000-g1234abcd Copyright", "N-121000-g1234abcd")]
    public void ParseVersion_reads_the_first_line(string output, string expected) =>
        Assert.Equal(expected, NativeTools.ParseVersion(output));

    [Fact]
    public void ParseVersion_returns_null_for_other_output() =>
        Assert.Null(NativeTools.ParseVersion("command not found"));

    [Fact]
    public async Task GetVersionAsync_returns_null_for_a_missing_tool() =>
        Assert.Null(await NativeTools.GetVersionAsync("ourcut-missing-" + Guid.NewGuid().ToString("N"),
            cancellationToken: TestContext.Current.CancellationToken));
}
