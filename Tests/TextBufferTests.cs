
using Xunit;
using Ctv;

namespace ctv.Tests;

public class TextBufferTests
{
    [Fact]
    public void Insert_AppendsText()
    {
        var buf = new TextBuffer();
        buf.Insert("hello");
        Assert.Equal("hello", buf.Text);
    }

    [Fact]
    public void Backspace_RemovesLastChar()
    {
        var buf = new TextBuffer();
        buf.Insert("hello");
        buf.Backspace();
        Assert.Equal("hell", buf.Text);
    }

    [Fact]
    public void Delete_RemovesCharAtCursor()
    {
        var buf = new TextBuffer();
        buf.Insert("hello");
        buf.SetCursor(2);
        buf.Delete();
        Assert.Equal("helo", buf.Text);
    }

    [Fact]
    public void MoveCursor_Left()
    {
        var buf = new TextBuffer();
        buf.Insert("hello");
        buf.SetCursor(5);
        buf.HandleKey(new System.ConsoleKeyInfo('h', System.ConsoleKey.LeftArrow, false, false, false));
        Assert.Equal(4, buf.Cursor);
    }
}
