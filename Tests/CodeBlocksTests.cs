
using Xunit;
using Ctv;

namespace ctv.Tests;

public class CodeBlocksTests
{
    [Fact]
    public void Parse_FindsFencedCodeBlocks()
    {
        var text = "Here is code:\n```csharp\nConsole.WriteLine(\"hi\");\n```\nDone.";
        var blocks = CodeBlocks.Parse(text);
        Assert.Single(blocks);
        Assert.Equal("csharp", blocks[0].Language);
    }

    [Fact]
    public void Parse_NoCodeBlocks_ReturnsEmpty()
    {
        var blocks = CodeBlocks.Parse("Just plain text.");
        Assert.Empty(blocks);
    }
}
