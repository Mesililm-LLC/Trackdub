using System.Xml.Linq;
using Trackdub.Inference.Onnx.Runtime;

namespace Trackdub.Inference.Tests;

public sealed class GenAiNativeCompatibilityTests
{
    [Fact]
    public void StaleWindowsMlMarkerDoesNotVerifyADeployment()
    {
        WithFiles((directory, ort, genAi) =>
        {
            File.WriteAllBytes(Path.Join(directory, "Microsoft.Windows.AI.MachineLearning.dll"), [0]);
            Assert.Equal(NativePairStatus.NotVerified,
                GenAiNativeRuntimeSelection.Verify(ort, genAi, "win-x64", []).Status);
        });
    }

    [Fact]
    public void MissingRuntimeIdentityIsNotCompatible()
    {
        Assert.Equal(NativePairStatus.NotVerified,
            GenAiNativeRuntimeSelection.Verify(null, null, "win-x64", []).Status);
    }

    [Fact]
    public void IntendedWindowsMlPairIsAcceptedWithoutManagedVersionEquality()
    {
        WithFiles((directory, ort, genAi) =>
        {
            var pair = Pair(ort, genAi);
            var result = GenAiNativeRuntimeSelection.Verify(ort, genAi, "win-x64", [pair]);
            Assert.Equal(NativePairStatus.Compatible, result.Status);
            Assert.Equal("1.27.1", result.Pair!.OrtVersion);
            Assert.Equal(Path.GetFullPath(ort), result.OrtPath);
        });
    }

    [Fact]
    public void UnsupportedNativePairIsRejected()
    {
        WithFiles((directory, ort, genAi) =>
        {
            var pair = Pair(ort, genAi) with { OrtVersion = "1.30.0", GenAiFlavor = "Cpu" };
            Assert.Equal(NativePairStatus.Incompatible,
                GenAiNativeRuntimeSelection.Verify(ort, genAi, "win-x64", [pair]).Status);
        });
    }

    [Fact]
    public void ReplacedNativeFileInvalidatesProvenance()
    {
        WithFiles((directory, ort, genAi) =>
        {
            var pair = Pair(ort, genAi);
            File.WriteAllBytes(ort, [9, 8, 7]);
            Assert.Equal(NativePairStatus.NotVerified,
                GenAiNativeRuntimeSelection.Verify(ort, genAi, "win-x64", [pair]).Status);
        });
    }

    [Fact]
    public void AlternateResolverDirectoryIsUsedAndOnlyCurrentArchitectureIsConsidered()
    {
        WithFiles((directory, ort, genAi) =>
        {
            string managed = Path.Join(directory, "managed");
            string native = Path.Join(managed, "runtimes", "win-arm64", "native");
            Directory.CreateDirectory(native);
            string alternate = Path.Join(native, "onnxruntime.dll");
            File.WriteAllBytes(alternate, [3]);
            Assert.Equal(Path.GetFullPath(alternate),
                GenAiNativeRuntimeSelection.FindCandidate(managed, directory, "win-arm64", "onnxruntime.dll"));
            Assert.Equal(Path.GetFullPath(ort),
                GenAiNativeRuntimeSelection.FindCandidate(managed, directory, "win-x64", "onnxruntime.dll"));
        });
    }

    [Theory]
    [InlineData("Cpu", "Microsoft.ML.OnnxRuntimeGenAI")]
    [InlineData("Cuda", "Microsoft.ML.OnnxRuntimeGenAI.Cuda")]
    public void StockOrtAtTheCentralPackagePinIsSupportedWithGenAi(string genAiFlavor, string genAiPackageId)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Join(directory, "Directory.Packages.props")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);
        XElement packageVersions = XDocument.Load(Path.Join(directory, "Directory.Packages.props")).Root!;
        string ortVersion = packageVersions.Descendants("OnnxRuntimeVersion").Single().Value.Trim();
        string genAiVersion = packageVersions.Descendants("PackageVersion")
            .Single(element => (string?)element.Attribute("Include") == genAiPackageId)
            .Attribute("Version")!.Value;
        var pair = new NativePairManifest("Stock", ortVersion, ortVersion, genAiFlavor, genAiVersion, "win-x64", "ort", "genai");

        Assert.True(GenAiNativeRuntimeSelection.IsSupported(pair),
            $"Update the stock ORT and GenAI allow-list in GenAiNativeRuntimeSelection for {genAiPackageId} {genAiVersion} with ORT {ortVersion}.");
        Assert.True(GenAiNativeRuntimeSelection.IsKnownOrt(pair),
            $"Update the stock ORT allow-list in GenAiNativeRuntimeSelection to {ortVersion}.");
    }

    private static NativePairManifest Pair(string ort, string genAi) =>
        new("WindowsML", "2.4.89", "1.27.1", "WinML", "0.17.1", "win-x64",
            GenAiNativeRuntimeSelection.HashFile(ort), GenAiNativeRuntimeSelection.HashFile(genAi));

    private static void WithFiles(Action<string, string, string> action)
    {
        string directory = Path.Join(Path.GetTempPath(), $"trackdub-native-pair-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string ort = Path.Join(directory, "onnxruntime.dll");
            string genAi = Path.Join(directory, "onnxruntime-genai.dll");
            File.WriteAllBytes(ort, [1, 2, 3]);
            File.WriteAllBytes(genAi, [4, 5, 6]);
            action(directory, ort, genAi);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
