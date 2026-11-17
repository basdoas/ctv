
using Xunit;
using Ctv;

namespace ctv.Tests;

public class AnsiTextTests
{
    [Fact]
    public void Width_PlainText_ReturnsLength()
    {
        Assert.Equal(5, AnsiText.Width("hello"));
    }

    [Fact]
    public void VisibleWidth_AnsiCodes_ReturnsVisibleLength()
    {
        Assert.Equal(5, AnsiText.VisibleWidth("\x1b[31mhello\x1b[0m"));
    }

    [Fact]
    public void Truncate_CutsAtMaxWithEllipsis()
    {
        var result = AnsiText.Truncate("hello world", 5);
        Assert.True(AnsiText.Width(result) <= 5);
    }

    [Fact]
    public void Wrap_BreaksLongWords()
    {
        var lines = AnsiText.Wrap("abcdefghij", 3);
        Assert.True(lines.Count > 1);
    }

    [Fact]
    public void Clean_RemovesControlChars()
    {
        var result = AnsiText.Clean("hello\x00world", true);
        Assert.Equal("helloworld", result);
    }
}
