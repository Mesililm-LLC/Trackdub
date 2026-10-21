using System.Text.Json.Nodes;
using Trackdub.Application.Transcripts;

namespace Trackdub.Application.Tests;

public sealed class ExportDeliveryTransactionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Failed_successor_preserves_prior_accepted_producer_identity(bool siblingContainer, bool restart)
    {
        using var fixture = new DeliveryFixture();
        using var accepted = fixture.CreateTransaction();
        await accepted.PrepareAsync(TestContext.Current.CancellationToken);
        accepted.Apply();
        accepted.Accept();
        string failedSource = System.IO.Path.Join(fixture.Path, "failed-replacement");
        File.WriteAllBytes(failedSource, [44, 45, 46]);
        string successorOutput = siblingContainer ? System.IO.Path.ChangeExtension(fixture.OutputPath, ".mkv") : fixture.OutputPath;
        using var failed = new ExportDeliveryTransaction(fixture.Replacements.Select((pair, index) =>
            (index == 0 ? successorOutput : pair.DestinationPath, failedSource)), Guid.NewGuid());
        await failed.PrepareAsync(TestContext.Current.CancellationToken);
        failed.Apply();
        if (restart)
        {
            failed.Dispose();
            ExportDeliveryTransaction.Recover(successorOutput);
        }
        else failed.Rollback();

        Assert.True(ExportDeliveryTransaction.IsAcceptedDelivery(fixture.OutputPath, fixture.Producer));
        foreach ((string destination, _) in fixture.Replacements)
            Assert.Equal(fixture.NewBytes, File.ReadAllBytes(destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Relocated_terminal_journal_allows_export_without_touching_original_paths(bool accepted)
    {
        using var fixture = new DeliveryFixture();
        using var transaction = fixture.CreateTransaction();
        await transaction.PrepareAsync(TestContext.Current.CancellationToken);
        transaction.Apply();
        if (accepted) transaction.Accept();
        else transaction.Rollback();
        byte[] originalBytes = File.ReadAllBytes(fixture.OutputPath);
        string originalBackup = System.IO.Path.Join(fixture.Path, $".lesson.mp4.{fixture.Producer:N}.bak");
        File.WriteAllBytes(originalBackup, [42]);
        string movedDirectory = System.IO.Path.Join(fixture.Path, "relocated");
        Directory.CreateDirectory(movedDirectory);
        foreach (string file in Directory.EnumerateFiles(fixture.Path))
            File.Copy(file, System.IO.Path.Join(movedDirectory, System.IO.Path.GetFileName(file)));
        string movedOutput = System.IO.Path.Join(movedDirectory, "lesson.mp4");
        using var next = new ExportDeliveryTransaction(fixture.Replacements.Select(pair =>
            (System.IO.Path.Join(movedDirectory, System.IO.Path.GetFileName(pair.DestinationPath)), pair.ReplacementPath)), Guid.NewGuid());

        await next.PrepareAsync(TestContext.Current.CancellationToken);
        next.Apply();
        next.Accept();
        ExportDeliveryTransaction.Recover(movedOutput);

        Assert.Equal(originalBytes, File.ReadAllBytes(fixture.OutputPath));
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(originalBackup));
        Assert.Equal(fixture.NewBytes, File.ReadAllBytes(movedOutput));
    }

    [Fact]
    public async Task Relocated_pending_journal_rejects_recovery_without_touching_either_family()
    {
        using var fixture = new DeliveryFixture();
        using var transaction = fixture.CreateTransaction();
        await transaction.PrepareAsync(TestContext.Current.CancellationToken);
        transaction.Apply();
        transaction.Dispose();
        string relocated = System.IO.Path.Join(fixture.Path, "relocated");
        Directory.CreateDirectory(relocated);
        foreach (string file in Directory.EnumerateFiles(fixture.Path))
            File.Copy(file, System.IO.Path.Join(relocated, System.IO.Path.GetFileName(file)));
        string movedOutput = System.IO.Path.Join(relocated, "lesson.mp4");
        string movedJournal = System.IO.Path.Join(relocated, ".lesson.export-delivery.json");
        byte[] journalBytes = File.ReadAllBytes(movedJournal);

        Assert.Throws<InvalidDataException>(() => ExportDeliveryTransaction.Recover(movedOutput));

        Assert.Equal(journalBytes, File.ReadAllBytes(movedJournal));
        foreach ((string destination, _) in fixture.Replacements)
        {
            Assert.Equal(fixture.NewBytes, File.ReadAllBytes(destination));
            Assert.Equal(fixture.NewBytes, File.ReadAllBytes(System.IO.Path.Join(relocated, System.IO.Path.GetFileName(destination))));
        }
        Assert.Equal(3, Directory.EnumerateFiles(fixture.Path, "*.bak").Count());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Foreign_terminal_journal_is_ignored_without_touching_its_family(bool accepted)
    {
        using var fixture = new DeliveryFixture();
        using var transaction = fixture.CreateTransaction();
        await transaction.PrepareAsync(TestContext.Current.CancellationToken);
        transaction.Apply();
        if (accepted) transaction.Accept();
        else transaction.Rollback();
        byte[] originalJournalBytes = File.ReadAllBytes(
            System.IO.Path.Join(fixture.Path, ".lesson.export-delivery.json"));
        // A terminal journal from another family at this location records no pending
        // work here: exporting must proceed and leave the foreign files alone.
        string otherOutput = System.IO.Path.Join(fixture.Path, "other.mp4");
        File.Copy(
            System.IO.Path.Join(fixture.Path, ".lesson.export-delivery.json"),
            System.IO.Path.Join(fixture.Path, ".other.export-delivery.json"));
        string otherSource = System.IO.Path.Join(fixture.Path, "other-replacement");
        byte[] otherBytes = [9, 10, 11, 12];
        File.WriteAllBytes(otherSource, otherBytes);
        using var other = new ExportDeliveryTransaction([(otherOutput, otherSource)], Guid.NewGuid());

        await other.PrepareAsync(TestContext.Current.CancellationToken);
        other.Apply();
        other.Accept();
        ExportDeliveryTransaction.Recover(otherOutput);

        Assert.Equal(otherBytes, File.ReadAllBytes(otherOutput));
        Assert.Equal(
            originalJournalBytes,
            File.ReadAllBytes(System.IO.Path.Join(fixture.Path, ".lesson.export-delivery.json")));
        byte[] expectedOriginal = accepted ? fixture.NewBytes : fixture.OldBytes;
        foreach ((string destination, _) in fixture.Replacements)
            Assert.Equal(expectedOriginal, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task Accepted_other_container_survives_stale_predecessor_recovery()
    {
        using var fixture = new DeliveryFixture();
        using var pendingMp4 = fixture.CreateTransaction();
        await pendingMp4.PrepareAsync(TestContext.Current.CancellationToken);
        pendingMp4.Apply();
        pendingMp4.Dispose();
        string mkvPath = System.IO.Path.ChangeExtension(fixture.OutputPath, ".mkv");
        string replacementPath = System.IO.Path.Join(fixture.Path, "mkv-replacement");
        byte[] mkvBytes = [10, 11, 12];
        File.WriteAllBytes(replacementPath, mkvBytes);
        Guid mkvProducer = Guid.NewGuid();
        var mkvReplacements = fixture.Replacements.Select((pair, index) =>
            (DestinationPath: index == 0 ? mkvPath : pair.DestinationPath, ReplacementPath: replacementPath));
        using var acceptedMkv = new ExportDeliveryTransaction(mkvReplacements, mkvProducer);
        await acceptedMkv.PrepareAsync(TestContext.Current.CancellationToken);
        acceptedMkv.Apply();
        acceptedMkv.Accept();

        ExportDeliveryTransaction.Recover(fixture.OutputPath);
        Assert.False(ExportDeliveryTransaction.IsAcceptedDelivery(fixture.OutputPath, fixture.Producer));
        Assert.True(ExportDeliveryTransaction.IsAcceptedDelivery(mkvPath, mkvProducer));
        Assert.Equal(mkvBytes, File.ReadAllBytes(mkvPath));
        foreach ((string destination, _) in fixture.Replacements.Skip(1))
            Assert.Equal(mkvBytes, File.ReadAllBytes(destination));
        Assert.Equal(fixture.OldBytes, File.ReadAllBytes(fixture.OutputPath));
    }

    [Fact]
    public async Task Active_other_container_blocks_overlapping_sidecar_writer()
    {
        using var fixture = new DeliveryFixture();
        using var mp4 = fixture.CreateTransaction();
        await mp4.PrepareAsync(TestContext.Current.CancellationToken);
        mp4.Apply();
        string mkvPath = System.IO.Path.ChangeExtension(fixture.OutputPath, ".mkv");
        using var mkv = new ExportDeliveryTransaction(fixture.Replacements.Select((pair, index) =>
            (index == 0 ? mkvPath : pair.DestinationPath, pair.ReplacementPath)), Guid.NewGuid());
        try
        {
            Assert.Throws<IOException>(() => ExportDeliveryTransaction.Recover(mkvPath));
            await Assert.ThrowsAsync<IOException>(() => mkv.PrepareAsync(TestContext.Current.CancellationToken));
            foreach ((string destination, _) in fixture.Replacements)
                Assert.Equal(fixture.NewBytes, File.ReadAllBytes(destination));
        }
        finally
        {
            mkv.Rollback();
            mp4.Rollback();
        }
    }

    [Fact]
    public async Task Recovery_cannot_mutate_an_active_delivery_transaction()
    {
        using var fixture = new DeliveryFixture();
        using var transaction = fixture.CreateTransaction();
        await transaction.PrepareAsync(TestContext.Current.CancellationToken);
        transaction.Apply();
        string journalPath = System.IO.Path.Join(fixture.Path, ".lesson.export-delivery.json");
        byte[] journalBytes = File.ReadAllBytes(journalPath);
        try
        {
            Assert.Throws<IOException>(() => ExportDeliveryTransaction.Recover(fixture.OutputPath));
            foreach ((string destination, _) in fixture.Replacements)
                Assert.Equal(fixture.NewBytes, File.ReadAllBytes(destination));
            Assert.Equal(journalBytes, File.ReadAllBytes(journalPath));
            using var competing = new ExportDeliveryTransaction(fixture.Replacements, Guid.NewGuid());
            await Assert.ThrowsAsync<IOException>(() => competing.PrepareAsync(TestContext.Current.CancellationToken));
            foreach ((string destination, _) in fixture.Replacements)
                Assert.Equal(fixture.NewBytes, File.ReadAllBytes(destination));
        }
        finally
        {
            transaction.Rollback();
        }
        fixture.AssertPriorDelivery();
    }

    [Fact]
    public async Task Restart_recovers_unaccepted_delivery()
    {
        using var fixture = new DeliveryFixture();
        using var transaction = fixture.CreateTransaction();
        await transaction.PrepareAsync(TestContext.Current.CancellationToken);
        transaction.Apply();
        transaction.Dispose(); // Simulate OS handle release when the process exits.
        Assert.Equal(fixture.NewBytes, File.ReadAllBytes(fixture.OutputPath));

        ExportDeliveryTransaction.Recover(fixture.OutputPath);
        ExportDeliveryTransaction.Recover(fixture.OutputPath);

        fixture.AssertPriorDelivery();
        Assert.False(ExportDeliveryTransaction.IsAcceptedDelivery(fixture.OutputPath, fixture.Producer));
        Assert.Empty(Directory.EnumerateFiles(fixture.Path, "*.bak"));
    }

    [Fact]
    public async Task Accepted_delivery_survives_restart()
    {
        using var fixture = new DeliveryFixture();
        using var transaction = fixture.CreateTransaction();
        await transaction.PrepareAsync(TestContext.Current.CancellationToken);
        transaction.Apply();
        transaction.Accept();

        ExportDeliveryTransaction.Recover(fixture.OutputPath);

        foreach ((string destination, _) in fixture.Replacements)
            Assert.Equal(fixture.NewBytes, File.ReadAllBytes(destination));
        Assert.True(ExportDeliveryTransaction.IsAcceptedDelivery(fixture.OutputPath, fixture.Producer));
        Assert.False(ExportDeliveryTransaction.IsAcceptedDelivery(fixture.OutputPath, Guid.NewGuid()));
        Assert.Empty(Directory.EnumerateFiles(fixture.Path, "*.bak"));
    }

    [Fact]
    public async Task Recovery_ignores_unrelated_output_families()
    {
        using var fixture = new DeliveryFixture();
        string unrelated = System.IO.Path.Join(fixture.Path, $".lesson-other.mp4.{Guid.NewGuid():N}.bak");
        string unknownSameFamilyBackup = System.IO.Path.Join(fixture.Path, $".lesson.mp4.{Guid.NewGuid():N}.bak");
        File.WriteAllBytes(unrelated, fixture.OldBytes);
        File.WriteAllBytes(unknownSameFamilyBackup, fixture.OldBytes);
        using var transaction = fixture.CreateTransaction();
        await transaction.PrepareAsync(TestContext.Current.CancellationToken);
        transaction.Apply();
        transaction.Dispose();

        ExportDeliveryTransaction.Recover(System.IO.Path.Join(fixture.Path, "lesson-other.mp4"));
        Assert.Equal(fixture.NewBytes, File.ReadAllBytes(fixture.OutputPath));
        ExportDeliveryTransaction.Recover(fixture.OutputPath);

        Assert.Equal(fixture.OldBytes, File.ReadAllBytes(unrelated));
        Assert.Equal(fixture.OldBytes, File.ReadAllBytes(unknownSameFamilyBackup));
        fixture.AssertPriorDelivery();
    }

    [Fact]
    public async Task Restart_recovers_interrupted_swap_before_replacement_move()
    {
        using var fixture = new DeliveryFixture();
        using var transaction = fixture.CreateTransaction();
        await transaction.PrepareAsync(TestContext.Current.CancellationToken);
        string backup = System.IO.Path.Join(fixture.Path, $".lesson.mp4.{fixture.Producer:N}.bak");
        File.Move(fixture.OutputPath, backup);
        transaction.Dispose();

        ExportDeliveryTransaction.Recover(fixture.OutputPath);

        fixture.AssertPriorDelivery();
        Assert.Empty(Directory.EnumerateFiles(fixture.Path, "*.tmp"));
    }

    [Fact]
    public async Task Restart_removes_unaccepted_delivery_without_previous_outputs()
    {
        using var fixture = new DeliveryFixture();
        foreach ((string destination, _) in fixture.Replacements) File.Delete(destination);
        using var transaction = fixture.CreateTransaction();
        await transaction.PrepareAsync(TestContext.Current.CancellationToken);
        transaction.Apply();
        transaction.Dispose();

        ExportDeliveryTransaction.Recover(fixture.OutputPath);

        foreach ((string destination, _) in fixture.Replacements) Assert.False(File.Exists(destination));
    }

    [Theory]
    [InlineData("DestinationPath")]
    [InlineData("TemporaryPath")]
    [InlineData("BackupPath")]
    public async Task Recovery_rejects_journal_paths_outside_output_family_without_touching_files(string field)
    {
        using var fixture = new DeliveryFixture();
        using var transaction = fixture.CreateTransaction();
        await transaction.PrepareAsync(TestContext.Current.CancellationToken);
        transaction.Apply();
        string unrelated = System.IO.Path.Join(fixture.Path, "lesson-other.mp4");
        File.WriteAllBytes(unrelated, fixture.OldBytes);
        string journalPath = System.IO.Path.Join(fixture.Path, ".lesson.export-delivery.json");
        JsonNode journal = JsonNode.Parse(File.ReadAllText(journalPath))!;
        journal["Entries"]![0]![field] = unrelated;
        File.WriteAllText(journalPath, journal.ToJsonString());
        transaction.Dispose();

        Assert.Throws<InvalidDataException>(() => ExportDeliveryTransaction.Recover(fixture.OutputPath));

        Assert.Equal(fixture.OldBytes, File.ReadAllBytes(unrelated));
        Assert.Equal(fixture.NewBytes, File.ReadAllBytes(fixture.OutputPath));
        Assert.Equal(3, Directory.EnumerateFiles(fixture.Path, "*.bak").Count());
    }

    [Fact]
    public async Task Retry_after_recovery_can_accept_new_delivery()
    {
        using var fixture = new DeliveryFixture();
        using var interrupted = fixture.CreateTransaction();
        await interrupted.PrepareAsync(TestContext.Current.CancellationToken);
        interrupted.Apply();
        interrupted.Dispose();
        using var retry = new ExportDeliveryTransaction(fixture.Replacements, Guid.NewGuid());
        await retry.PrepareAsync(TestContext.Current.CancellationToken);
        fixture.AssertPriorDelivery();
        retry.Apply();
        retry.Accept();

        ExportDeliveryTransaction.Recover(fixture.OutputPath);

        foreach ((string destination, _) in fixture.Replacements)
            Assert.Equal(fixture.NewBytes, File.ReadAllBytes(destination));
    }

    private sealed class DeliveryFixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Join(System.IO.Path.GetTempPath(), $"export-delivery-{Guid.NewGuid():N}");
        public Guid Producer { get; } = Guid.NewGuid();
        public byte[] OldBytes { get; } = [1, 2, 3, 4];
        public byte[] NewBytes { get; } = [5, 6, 7, 8];
        public string OutputPath => System.IO.Path.Join(Path, "lesson.mp4");
        public (string DestinationPath, string ReplacementPath)[] Replacements { get; }

        public DeliveryFixture()
        {
            Directory.CreateDirectory(Path);
            string source = System.IO.Path.Join(Path, "replacement");
            File.WriteAllBytes(source, NewBytes);
            Replacements = [ (OutputPath, source),
                (System.IO.Path.ChangeExtension(OutputPath, ".srt"), source),
                (System.IO.Path.ChangeExtension(OutputPath, ".export-manifest.json"), source) ];
            foreach ((string destination, _) in Replacements) File.WriteAllBytes(destination, OldBytes);
        }

        public ExportDeliveryTransaction CreateTransaction() => new(Replacements, Producer);
        public void AssertPriorDelivery()
        {
            foreach ((string destination, _) in Replacements) Assert.Equal(OldBytes, File.ReadAllBytes(destination));
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
