
using Xunit;
using Ctv;

namespace ctv.Tests;

public class TodoToolsTests
{
    [Fact]
    public void HasTodoCalls_DetectsTags()
    {
        Assert.True(TodoTools.HasTodoCalls("<todo_add>Buy milk</todo_add>"));
        Assert.False(TodoTools.HasTodoCalls("No tags here"));
    }
}
