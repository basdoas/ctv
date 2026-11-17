
using Xunit;
using Ctv;

namespace ctv.Tests;

public class PathResolverTests
{
    [Fact]
    public void Resolve_TildeExpandsToHome()
    {
        var result = PathResolver.Resolve("~");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(home, result);
    }

    [Fact]
    public void Resolve_RelativePath_ReturnsFullPath()
    {
        var result = PathResolver.Resolve("./foo.txt");
        Assert.EndsWith("foo.txt", result);
    }
}
