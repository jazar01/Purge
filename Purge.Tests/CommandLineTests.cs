namespace Purge.Tests;

public class CommandLineTests
{
    [Theory]
    [InlineData("-n", "-1")]
    [InlineData("-d", "-1")]
    [InlineData("-l", "51")]
    [InlineData("-s", "-1")]
    public void InvalidValues_AreParseErrors(string option, string value)
    {
        var result = Program.BuildRootCommand().Parse([option, value, "*.zip"]);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void AllOptions_Parse()
    {
        var result = Program.BuildRootCommand().Parse(
            ["-n", "5", "-d", "30", "-s", "3", "-t", "-f", "-p", @"C:\Backups\*.zip"]);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void MissingFileSpec_IsParseError()
    {
        Assert.NotEmpty(Program.BuildRootCommand().Parse(["-n", "5"]).Errors);
    }
}
