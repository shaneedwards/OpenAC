using AcDream.Runtime.Plugins;

namespace AcDream.HostParity.Tests;

/// <summary>Pins the parity scratch layout against a macOS-shaped temp root.</summary>
public sealed class ParityScratchPathPinTests
{
    private const string MacTempRoot =
        "/var/folders/17/0d2n_dmn4gg8cfvxrhn6yg4c0000gn/T/";

    [Fact]
    public void ScratchSocketPathFitsUnderThePlatformLimit()
    {
        string directory = ParityArm.ScratchDirectory(MacTempRoot);
        string socketPath = PeerHubEndpoint.ForDirectory(
            Path.Combine(directory, "plugin-peers")).SocketPath;

        // A drive prefix Path.GetFullPath adds on this host is not part of a
        // macOS path, so only what follows the recognisable folder counts.
        int anchor = socketPath.IndexOf(
            "acdream-parity", StringComparison.Ordinal);
        Assert.True(anchor >= 0, socketPath);
        int tailBytes = System.Text.Encoding.UTF8.GetByteCount(
            socketPath[anchor..]);

        // macOS's sun_path is 104 bytes including the terminating NUL.
        Assert.InRange(MacTempRoot.Length + tailBytes, 1, 103);
    }
}
