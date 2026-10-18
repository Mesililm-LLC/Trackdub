using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DubBench.Services;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;
using Trackdub.Domain.Benchmarking;

namespace DubBench.ViewModels;

public sealed partial class OnnxModelTabViewModel : ObservableObject, ITabViewModel
{
    private readonly IBenchmarkRunnerService _runner;
    private readonly IOliveOptimizationService _olive;

    public string Title => "ONNX Model";
    public string IconGlyph => "\U0001F52C";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isDevicePolicyVisible = OperatingSystem.IsWindows();

    // Provider selection
    public ObservableCollection<BenchmarkProviderPreference> Providers { get; } = CreateProvidersForCurrentPlatform();

    [ObservableProperty]
    private BenchmarkProviderPreference _selectedProvider = BenchmarkProviderPreference.Auto;

    // Device policy selection (Windows ML catalog routes only)
    public ObservableCollection<WindowsMlExecutionDevicePolicy> DevicePolicies { get; } =
        CreateDevicePoliciesForCurrentPlatform();

    [ObservableProperty]
    private WindowsMlExecutionDevicePolicy _selectedDevicePolicy = WindowsMlExecutionDevicePolicy.Explicit;

    // Model path
    [ObservableProperty]
    private string _modelPath = string.Empty;

    // Run count
    [ObservableProperty]
    private int _runCount = 5;

    // Running state
    [ObservableProperty]
    private bool _isRunning;

    // Olive optimization
    [ObservableProperty]
    private bool _oliveAvailable;

    [ObservableProperty]
    private bool _enableOliveOptimization;

    [ObservableProperty]
    private bool _isOptimizing;

    [ObservableProperty]
    private string _optimizedModelPath = string.Empty;

    // Result
    [ObservableProperty]
    private BenchmarkReport? _lastResult;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    // Persisted ONNX benchmark history (most recent first)
    public ObservableCollection<OnnxBenchmarkHistoryEntry> RecentRuns { get; } = new();

    [ObservableProperty]
    private bool _isHistoryLoading;

    public OnnxModelTabViewModel()
        : this(new BenchmarkRunnerService(), new OliveOptimizationService())
    {
    }

    public OnnxModelTabViewModel(IBenchmarkRunnerService runner, IOliveOptimizationService olive)
    {
        _runner = runner;
        _olive = olive;
        if (!Providers.Contains(SelectedProvider))
        {
            SelectedProvider = BenchmarkProviderPreference.Auto;
        }

        _ = ProbeOliveSafeAsync();
        _ = RefreshHistoryAsync();
    }

    private static ObservableCollection<BenchmarkProviderPreference> CreateProvidersForCurrentPlatform()
    {
        var providers = new ObservableCollection<BenchmarkProviderPreference>
        {
            BenchmarkProviderPreference.Auto,
            BenchmarkProviderPreference.Cpu,
        };

        if (OperatingSystem.IsWindows())
        {
            providers.Add(BenchmarkProviderPreference.Dml);
            providers.Add(BenchmarkProviderPreference.TensorRtRtx);
            providers.Add(BenchmarkProviderPreference.Migraphx);
            providers.Add(BenchmarkProviderPreference.Cuda);
            providers.Add(BenchmarkProviderPreference.TensorRt);
        }
        else if (OperatingSystem.IsLinux())
        {
            providers.Add(BenchmarkProviderPreference.TensorRtRtx);
            providers.Add(BenchmarkProviderPreference.Migraphx);
            providers.Add(BenchmarkProviderPreference.Cuda);
            providers.Add(BenchmarkProviderPreference.TensorRt);
        }

        return providers;
    }

    private static ObservableCollection<WindowsMlExecutionDevicePolicy> CreateDevicePoliciesForCurrentPlatform()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        return
        [
            WindowsMlExecutionDevicePolicy.Explicit,
            WindowsMlExecutionDevicePolicy.MaxPerformance,
            WindowsMlExecutionDevicePolicy.PreferNpu,
            WindowsMlExecutionDevicePolicy.MaxEfficiency,
            WindowsMlExecutionDevicePolicy.MinOverallPower,
        ];
    }

    private async Task ProbeOliveSafeAsync()
    {
        try
        {
            // Intentionally no ConfigureAwait(false): result written to an observable property on the UI thread.
            OliveAvailable = await _olive.ProbeAvailabilityAsync();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            OliveAvailable = false;
            StatusMessage = "Olive CLI not available on PATH.";
        }
    }

    [RelayCommand]
    private async Task BrowseModelAsync()
    {
        StatusMessage = "Select model file via OS file dialog (requires View integration)";
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task OptimizeModelAsync()
    {
        if (IsOptimizing || string.IsNullOrEmpty(ModelPath)) return;

        try
        {
            IsOptimizing = true;
            StatusMessage = "Running Olive optimization...";

            var outputDir = Path.Join(
                Path.GetTempPath(), "DubBench", "olive",
                Path.GetFileNameWithoutExtension(ModelPath));

            var result = await _olive.OptimizeAsync(ModelPath, outputDir);

            if (result is not null)
            {
                OptimizedModelPath = result;
                StatusMessage = $"Optimized model saved to {Path.GetFileName(result)}";
            }
            else
            {
                StatusMessage = "Olive optimization failed — check that Olive CLI is on PATH";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Optimization error: {ex.Message}";
        }
        finally
        {
            IsOptimizing = false;
        }
    }

    [RelayCommand]
    private async Task RunBenchmarkAsync()
    {
        if (IsRunning) return;

        try
        {
            IsRunning = true;

            var actualModelPath = !string.IsNullOrEmpty(OptimizedModelPath) && EnableOliveOptimization
                ? OptimizedModelPath
                : ModelPath;

            if (string.IsNullOrWhiteSpace(actualModelPath) || !File.Exists(actualModelPath))
            {
                StatusMessage = "No model file selected or file does not exist. Choose a model first.";
                return;
            }

            StatusMessage = $"Running ONNX model benchmark on: {Path.GetFileName(actualModelPath)}...";

            var request = new BenchmarkRequest(
                ModelPath: actualModelPath,
                ReportPath: Path.Join(Path.GetTempPath(), "DubBench", $"onnx-report-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json"),
                ProviderPreference: SelectedProvider,
                RunCount: RunCount,
                WindowsMlDevicePolicyKey: WindowsMlExecutionDevicePolicySettings.ToKey(SelectedDevicePolicy));

            LastResult = await _runner.RunOnnxModelBenchmarkAsync(request);
            StatusMessage = LastResult.Status switch
            {
                BenchmarkStatus.Completed => $"Completed \u2014 Selected: {LastResult.SelectedProvider}" +
                    (LastResult.Measurements.WarmLatencyAverageMilliseconds.HasValue
                        ? $", Avg: {LastResult.Measurements.WarmLatencyAverageMilliseconds.Value:F1}ms"
                        : string.Empty),
                BenchmarkStatus.Failed => $"Failed: {LastResult.FailureReason ?? "Unknown error"}",
                _ => "Unknown status"
            };
            if (LastResult.HistoryPersistence?.Status is BenchmarkHistoryPersistenceStatus.Failed)
            {
                StatusMessage += $" (history save failed: {LastResult.HistoryPersistence.FailureReason})";
            }

            await RefreshHistoryAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private async Task RefreshHistoryAsync()
    {
        if (IsHistoryLoading)
        {
            return;
        }

        try
        {
            IsHistoryLoading = true;
            IReadOnlyList<BenchmarkRunRecord> records = await _runner.ListOnnxBenchmarkHistoryAsync(50);
            RecentRuns.Clear();
            foreach (BenchmarkRunRecord record in records)
            {
                RecentRuns.Add(OnnxBenchmarkHistoryEntry.From(record));
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load ONNX benchmark history: {ex.Message}";
        }
        finally
        {
            IsHistoryLoading = false;
        }
    }
}

public sealed record OnnxBenchmarkHistoryEntry(
    Guid RunId,
    string Model,
    string Provider,
    string Status,
    string Average,
    string P95,
    string Samples,
    string TimingMode,
    DateTimeOffset GeneratedAtUtc)
{
    public static OnnxBenchmarkHistoryEntry From(BenchmarkRunRecord record) =>
        new(
            record.Id,
            FormatModelId(record.ModelId),
            record.SelectedProvider,
            record.Status.ToString(),
            record.WarmLatencyAverageMilliseconds is { } avg ? $"{avg:F1} ms" : "n/a",
            record.WarmLatencyP95Milliseconds is { } p95 ? $"{p95:F1} ms" : "n/a",
            record.SampleCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a",
            record.TimingMode?.ToString() ?? "n/a",
            record.GeneratedAtUtc);

    private static string FormatModelId(string modelId) =>
        modelId.StartsWith("hardware-profiler:", StringComparison.Ordinal)
            ? modelId.Split(':', 4) is { Length: >= 4 } parts ? $"profiler:{parts[3]}" : modelId
            : modelId;
}
