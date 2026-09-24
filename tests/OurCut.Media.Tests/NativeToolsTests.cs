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
