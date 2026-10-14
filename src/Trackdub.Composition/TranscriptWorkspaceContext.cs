using Trackdub.Contracts;

namespace Trackdub.Composition;

public sealed class TranscriptWorkspaceContext : ITranscriptWorkspaceContext
{
    private bool initialized;
    private string? projectRootPath;
    private StudioSettings? settings;

    public string ProjectRootPath
    {
        get
        {
            ThrowIfNotInitialized();
            return projectRootPath!;
        }
    }

    public StudioSettings Settings
    {
        get
        {
            ThrowIfNotInitialized();
            return settings!;
        }
    }

    /// <summary>
    /// Returns the per-session settings without throwing when the context belongs to a
    /// bare scope (e.g. root-level readiness evaluation with no session). Returns false
    /// and a default snapshot when the context has not been initialized.
    /// </summary>
    public bool TryGetSettings(out StudioSettings settings)
    {
        if (!initialized)
        {
            settings = StudioSettings.Default;
            return false;
        }

        settings = this.settings!;
        return true;
    }

    public void Initialize(string projectRootPath, StudioSettings? settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
        if (initialized)
        {
            throw new InvalidOperationException("Transcript workspace context has already been initialized.");
        }

        this.projectRootPath = Path.GetFullPath(projectRootPath);
        this.settings = settings ?? StudioSettings.Default;
        initialized = true;
    }

    private void ThrowIfNotInitialized()
    {
        if (!initialized)
        {
            throw new InvalidOperationException("Transcript workspace context has not been initialized.");
        }
    }
}
