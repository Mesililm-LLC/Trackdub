using Trackdub.Contracts;
using Trackdub.Contracts.Projects;
using Trackdub.Domain.Editing;

namespace Trackdub.Application.Editing;

/// <summary>Stores the sequence independently of the legacy mix timeline and project manifest.</summary>
public sealed class SequenceStore(IArtifactStore artifactStore)
{
    private readonly IArtifactStore artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));

    public async Task<SequenceDocument?> LoadAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project id is required.", nameof(projectId));
        }
        if (!artifactStore.Exists(ProjectArtifactPaths.SequenceRelativePath))
        {
            return null;
        }
        SequenceDocument document = await artifactStore.ReadJsonAsync<SequenceDocument>(
            ProjectArtifactPaths.SequenceRelativePath, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The saved sequence is empty.");
        SequenceEditor.Validate(document);
        if (document.ProjectId != projectId)
        {
            throw new InvalidDataException("The saved sequence belongs to a different project.");
        }
        return document;
    }

    public async Task SaveAsync(SequenceDocument document, CancellationToken cancellationToken)
    {
        SequenceEditor.Validate(document);
        // Do not overwrite a corrupt document, a future schema, or another project's sequence.
        _ = await LoadAsync(document.ProjectId, cancellationToken).ConfigureAwait(false);
        await artifactStore.WriteJsonAsync(ProjectArtifactPaths.SequenceRelativePath, document, cancellationToken)
            .ConfigureAwait(false);
    }
}
