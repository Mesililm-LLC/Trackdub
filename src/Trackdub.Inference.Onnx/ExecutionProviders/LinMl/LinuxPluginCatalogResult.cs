namespace Trackdub.Inference.Onnx.ExecutionProviders.LinMl;

public enum LinuxPluginCatalogStatus
{
    /// <summary>Registered with ONNX Runtime and exposing at least one device.</summary>
    Ready,

    /// <summary>The plugin is not installed and downloads were not allowed.</summary>
    NotInstalled,

    /// <summary>Installed and registered, but the plugin exposes no device on this machine.</summary>
    NoDevices,

    /// <summary>Cannot work here: wrong OS/architecture, ORT too old, or no catalog support.</summary>
    Unsupported,

    /// <summary>Download, verification or registration failed, or preparation timed out.</summary>
    Failed,
}

/// <summary>Outcome of preparing a plugin EP through the Linux (LinML) catalog.</summary>
public sealed record LinuxPluginCatalogResult(LinuxPluginCatalogStatus Status, string Detail)
{
    public bool IsReady => Status is LinuxPluginCatalogStatus.Ready;
}
