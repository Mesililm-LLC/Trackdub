using Trackdub.Application.Editing;
using Trackdub.Contracts.Projects;
using Trackdub.Domain.Editing;
using Trackdub.Infrastructure.FileSystem;

namespace Trackdub.Infrastructure.Tests;

public sealed class SequenceStoreTests
{
    [Fact]
    public async Task Save_reopen_and_reject_future_version_without_overwriting_it()
    {
        string root = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifacts = new FileSystemArtifactStore(root);
            var store = new SequenceStore(artifacts);
            Guid projectId = Guid.NewGuid();
            CancellationToken ct = TestContext.Current.CancellationToken;
            await artifacts.EnsureLayoutAsync(ct);
            Assert.Null(await store.LoadAsync(projectId, ct));
            Assert.False(artifacts.Exists(ProjectArtifactPaths.SequenceRelativePath));
            var source = new SequenceSource(Guid.NewGuid(), projectId, Guid.NewGuid(), null, 120, 1920, 1080);
            var child = new SequenceSource(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "sources/child.trackdub", 40, 1080, 1920);
            SequenceDocument document = SequenceEditor.AddSource(SequenceDocument.Create(projectId), source);
            document = SequenceEditor.AddSource(document, child);
            document = SequenceEditor.AddPlacement(document, new SequencePlacement(Guid.NewGuid(), child.Id, 3, 18, new SequenceCrop(1.5, 0.4, 0.6)));
            document = SequenceEditor.AddPlacement(document, new SequencePlacement(Guid.NewGuid(), child.Id, 20, 30, new SequenceCrop()));
            await store.SaveAsync(document, ct);

            SequenceDocument reloaded = (await store.LoadAsync(projectId, ct))!;
            Assert.Equal(document.Frame, reloaded.Frame);
            Assert.Equal(document.Sources, reloaded.Sources);
            Assert.Equal(document.Placements, reloaded.Placements);

            SequenceDocument future = document with { Version = SequenceDocument.CurrentVersion + 1 };
            await artifacts.WriteJsonAsync(ProjectArtifactPaths.SequenceRelativePath, future, ct);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(projectId, ct));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(document, ct));
            Assert.Equal(future.Version, (await artifacts.ReadJsonAsync<SequenceDocument>(ProjectArtifactPaths.SequenceRelativePath, ct))!.Version);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
