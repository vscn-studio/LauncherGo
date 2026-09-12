using ServerMap.Web;
using Xunit;

namespace LauncherGo.Tests;

public sealed class TeleportAccessTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void HiddenRegionsAlwaysBlockOrdinaryPlayers(bool fog, bool explored)
    {
        var queried = false;
        var error = TeleportAccess.Denial(new() { FogEnabled = fog }, false, true, () => { queried = true; return explored; });
        Assert.Equal(TeleportAccess.HiddenRegion, error); Assert.False(queried);
    }

    [Fact]
    public void EnabledFogRequiresEffectiveExploration()
    {
        Assert.Equal(TeleportAccess.Unexplored, TeleportAccess.Denial(new(), false, false, () => false));
        Assert.Null(TeleportAccess.Denial(new(), false, false, () => true));
        Assert.Null(TeleportAccess.Denial(new() { FogEnabled = false }, false, false, () => throw new Exception("Fog is off")));
    }

    [Fact]
    public void AdminFogExceptionDoesNotIgnoreDisabledBypass()
    {
        Assert.Null(TeleportAccess.Denial(new(), true, true, () => throw new Exception("Admin bypass")));
        Assert.Equal(TeleportAccess.Unexplored, TeleportAccess.Denial(new() { AdminsBypassFog = false }, true, false, () => false));
        Assert.Null(TeleportAccess.Denial(new() { AdminsBypassFog = false }, true, true, () => true));
    }
}
