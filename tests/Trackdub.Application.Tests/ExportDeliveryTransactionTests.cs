using System.Text.Json.Nodes;
using Trackdub.Application.Transcripts;

namespace Trackdub.Application.Tests;

public sealed class ExportDeliveryTransactionTests
{
    [Fact]
    public async Task Recovery_cannot_mutate_an_active_delivery_transaction()
    {
        using var fixture = new DeliveryFixture();
        using var transaction = fixture.CreateTransaction();
        await transaction.PrepareAsync(TestContext.Current.CancellationToken);
        transaction.Apply();
        string journalPath = System.IO.Path.Join(fixture.Path, ".lesson.mp4.export-delivery.json");
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
        string journalPath = System.IO.Path.Join(fixture.Path, ".lesson.mp4.export-delivery.json");
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
