using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Domain;
using Trackdub.Sdk.Composition;
using Microsoft.Extensions.DependencyInjection;

namespace Trackdub.Sdk;

/// <summary>
/// Fluent configuration entry point for the headless Trackdub SDK.
/// Collects settings and produces a <see cref="TrackdubSessionFactory"/>.
/// </summary>
public sealed class TrackdubBuilder
{
    private string? _modelDirectory;
    private string? _modelCacheDirectory;
    private string? _logDirectory;
    private ExecutionProviderKind? _preferredExecutionProvider;
    private bool _requirePreferredExecutionProvider;
    private WindowsMlExecutionDevicePolicy _windowsMlExecutionDevicePolicy = WindowsMlExecutionDevicePolicy.Explicit;
    private string? _ffmpegPath;
    private string? _ffprobePath;
    private IApplicationLogger? _logger;
    private Action<IServiceCollection>? _serviceConfigurator;

    /// <summary>
    /// Sets the path to the directory containing downloaded models.
    /// </summary>
    /// <param name="path">Absolute path to the model directory.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public TrackdubBuilder WithModelDirectory(string path)
    {
        _modelDirectory = path;
        return this;
    }

    /// <summary>
    /// Sets the path to the model cache directory.
    /// </summary>
    /// <param name="path">Absolute path to the model cache directory.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public TrackdubBuilder WithModelCacheDirectory(string path)
    {
        _modelCacheDirectory = path;
        return this;
    }

    /// <summary>
    /// Sets the path to the log output directory.
    /// </summary>
    /// <param name="path">Absolute path to the log directory.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public TrackdubBuilder WithLogDirectory(string path)
    {
        _logDirectory = path;
        return this;
    }

    /// <summary>
    /// Sets the preferred execution provider for inference using the legacy four-value enum.
    /// On Windows, <see cref="ExecutionProviderPreference.Cuda"/> maps to TensorRT RTX.
    /// Soft prefer: the planner may fall through to another allowed EP when the preferred
    /// provider is unavailable, fails smoke, or TensorRT RTX session initialization fails.
    /// </summary>
    /// <param name="preference">The execution provider preference.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public TrackdubBuilder WithExecutionProvider(ExecutionProviderPreference preference)
    {
        _preferredExecutionProvider = ExecutionProviderPreferenceMapping.ToPreferredKind(preference);
        _requirePreferredExecutionProvider = false;
        return this;
    }

    /// <summary>
    /// Prefers a specific <see cref="ExecutionProviderKind"/> without requiring it.
    /// (for example TensorRT RTX → DirectML → CPU) when the preferred EP is forbidden for the engine family
    /// or fails smoke. TensorRT RTX session-init fallback applies only to supported TensorRT RTX
    /// failures (unsupported ops/kernels); other providers do not retry on session-init failure.
    /// the engine family, fails smoke, or TensorRT RTX session initialization fails.
    /// Pass nothing / use Auto for unconstrained planner choice.
    /// Use <see cref="WithExecutionProvider(ExecutionProviderKind, bool)"/> with
    /// <c>require: true</c> (or CLI <c>--require-execution-provider</c>) for a hard pin.
    /// </summary>
    /// <param name="provider">The execution provider kind to prefer.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public TrackdubBuilder WithExecutionProvider(ExecutionProviderKind provider) =>
        WithExecutionProvider(provider, require: false);

    /// <summary>
    /// Sets the preferred execution provider kind, optionally requiring it when the stage
    /// allow-list includes that provider.
    /// </summary>
    /// <param name="provider">The execution provider kind to prefer or require.</param>
    /// <param name="require">
    /// When <see langword="true"/>, stages that allow this provider collapse to it only and
    /// block on smoke/init failure. When <see langword="false"/>, prefer then fall through.
    /// </param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public TrackdubBuilder WithExecutionProvider(ExecutionProviderKind provider, bool require)
    {
        _preferredExecutionProvider = provider;
        _requirePreferredExecutionProvider = require;
        return this;
    }

    /// <summary>
    /// Sets the Windows ML execution-provider device policy (advanced). Windows-only; ignored on
    /// other platforms. <see cref="WindowsMlExecutionDevicePolicy.Explicit"/> (default) keeps
    /// Trackdub's own explicit catalog device selection instead of delegating to ORT's
    /// <c>SetEpSelectionPolicy</c>.
    /// </summary>
    /// <param name="policy">The Windows ML device policy.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public TrackdubBuilder WithWindowsMlExecutionDevicePolicy(WindowsMlExecutionDevicePolicy policy)
    {
        _windowsMlExecutionDevicePolicy = policy;
        return this;
    }

    /// <summary>
    /// Sets the path to the FFmpeg executable and optionally FFprobe.
    /// </summary>
    /// <param name="ffmpegPath">Path to the FFmpeg executable.</param>
    /// <param name="ffprobePath">Optional path to the FFprobe executable.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public TrackdubBuilder WithFfmpegPath(string? ffmpegPath, string? ffprobePath = null)
    {
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
        return this;
    }

    /// <summary>
    /// Sets a custom application logger to replace the default rolling file logger.
    /// </summary>
    /// <param name="logger">The logger implementation to use.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public TrackdubBuilder WithLogger(IApplicationLogger logger)
    {
        _logger = logger;
        return this;
    }

    /// <summary>
    /// Provides a delegate to override or extend DI registrations.
    /// </summary>
    /// <param name="configure">Action invoked during container build to customize services.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public TrackdubBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        _serviceConfigurator = configure;
        return this;
    }

    /// <summary>
    /// Validates configuration and builds a <see cref="TrackdubSessionFactory"/>.
    /// </summary>
    /// <returns>A thread-safe factory for creating dubbing sessions.</returns>
    /// <exception cref="DirectoryNotFoundException">
    /// Thrown when <see cref="WithModelDirectory"/> specifies a path that does not exist.
    /// </exception>
    public TrackdubSessionFactory Build()
    {
        // Validate model directory exists if specified.
        if (_modelDirectory is not null && !Directory.Exists(_modelDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Model directory not found: {_modelDirectory}");
        }

        // Validate FFmpeg path exists if specified.
        if (_ffmpegPath is not null && !File.Exists(_ffmpegPath))
        {
            throw new FileNotFoundException(
                $"FFmpeg executable not found: {_ffmpegPath}", _ffmpegPath);
        }

        var options = new TrackdubOptions
        {
            ModelDirectory = _modelDirectory,
            ModelCacheDirectory = _modelCacheDirectory,
            LogDirectory = _logDirectory,
            PreferredExecutionProvider = _preferredExecutionProvider,
            RequirePreferredExecutionProvider = _requirePreferredExecutionProvider,
            WindowsMlExecutionDevicePolicy = _windowsMlExecutionDevicePolicy,
            FfmpegPath = _ffmpegPath,
            FfprobePath = _ffprobePath,
            Logger = _logger,
            ServiceConfigurator = _serviceConfigurator,
        };

        var services = new ServiceCollection();
        HeadlessCompositionRoot.AddHeadlessTrackdub(services, options);

        ServiceProvider serviceProvider = services.BuildServiceProvider();

        // This composition owns its ServiceProvider instead of running through a Generic Host,
        // so activate hosted startup services explicitly.
        serviceProvider.GetRequiredService<Trackdub.Composition.Pipeline.EngineAutoWarmInstaller>()
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        // The SDK builds the headless composition directly (not through HeadlessDubbingHost),
        // so it must hand the process-GPU reader to the shared ONNX session pool itself or the
        // advertised default-on accelerator admission silently stays reservation-only here.
        IProcessGpuMemoryReader? processGpuReader =
            HeadlessCompositionRoot.BindSharedPoolProcessGpuAdmission(serviceProvider);

        IReadOnlyDictionary<int, long>? adapterLuidMap =
            HeadlessCompositionRoot.BindSharedPoolAdapterLuidMap(serviceProvider);

        return new TrackdubSessionFactory(serviceProvider, processGpuReader, adapterLuidMap);
    }
}
