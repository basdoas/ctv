
using Xunit;
using Ctv;

namespace ctv.Tests;

public class SafetyPoliciesTests
{
    [Fact]
    public void CheckShellCommand_SafeCommandsPass()
    {
        var (dangerous, _) = SafetyPolicies.CheckShellCommand("ls -la");
        Assert.False(dangerous);
    }

    [Fact]
    public void CheckShellCommand_DetectsRecursiveDelete()
    {
        var (dangerous, _) = SafetyPolicies.CheckShellCommand("delete-all-files");
        // "delete-all-files" contains "delete" not in regex, so let's test actual pattern
        var (dangerous2, _) = SafetyPolicies.CheckShellCommand("sudo rm -rf /tmp");
        Assert.True(dangerous2);
    }
}
