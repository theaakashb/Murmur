public class SmokeTests
{
    [Fact]
    public void RunsOnWindows() => Assert.True(OperatingSystem.IsWindows());
}
