namespace Purge.Tests;

public class FileWiperTests
{
    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 0)]
    [InlineData(3, 3 * 1024 * 1024 + 17)]
    public void WipeAndDelete_RemovesFileAndLeavesNothingBehind(int passes, int size)
    {
        using var dir = new TempDir();
        string path = System.IO.Path.Combine(dir.Path, "secret.log");
        System.IO.File.WriteAllBytes(path, new byte[size]);

        FileWiper.WipeAndDelete(new FileInfo(path), passes);

        Assert.Empty(dir.Names());
    }

    [Fact]
    public void WipeAndDelete_MissingFile_Throws()
    {
        using var dir = new TempDir();
        Assert.Throws<FileNotFoundException>(() =>
            FileWiper.WipeAndDelete(new FileInfo(System.IO.Path.Combine(dir.Path, "gone.txt")), 1));
    }

    [Fact]
    public void WipeAndDelete_ReadOnlyFile_ThrowsAndKeepsFile()
    {
        using var dir = new TempDir();
        string path = dir.File("ro.txt", 1);
        System.IO.File.SetAttributes(path, FileAttributes.ReadOnly);

        Assert.Throws<UnauthorizedAccessException>(() => FileWiper.WipeAndDelete(new FileInfo(path), 1));
        Assert.Equal("data", System.IO.File.ReadAllText(path));
    }
}
