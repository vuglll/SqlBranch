using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>
/// Task 5.3: every case of tasks 5.1 and 5.2 in JSON and in text, then everything they printed or left behind is searched for the
/// access key ID, the secrets and the SAS token: standard output and error, the archives (raw and unpacked), every manifest, the engine
/// container's settings and every <c>docker</c> command line the tool ran.
/// </summary>
[Collection(CloudSourceGroup.Name)]
[Trait("Category", "Integration")]
public sealed class ImageArchiveSecretLeakIntegrationTests(CloudSourceFixture fixture, ITestOutputHelper output)
{
    [IntegrationFact]
    public async Task No_storage_secret_is_in_any_output_archive_manifest_command_line_or_the_engine_settings()
    {
        var recorder = new OutputRecorder();
        var scenarios = new ImageArchiveScenarios(fixture, output, recorder);
        foreach (var json in new[] { true, false })
        {
            await scenarios.FileRoundTripAsync(json);
            await scenarios.ExistingDestinationAsync(json);
            await scenarios.CorruptAndTruncatedAsync(json);
            await scenarios.NotEnoughSpaceAsync(json);
            await scenarios.ImageRemovedDuringExportAsync(json);
            if (ImageArchiveScenarios.PerlAvailable)
            {
                await scenarios.CancelledImportAsync(json);
            }

            await scenarios.KilledImportAsync(json);
            await scenarios.S3RoundTripAsync(json);
            await scenarios.S3CancelledUploadAsync(json);
            await scenarios.S3ObjectCreatedDuringUploadAsync(json);
            await scenarios.S3WrongSecretAsync(json);
            await scenarios.BlobRoundTripAsync(json);
            await scenarios.BlobWrongSasAsync(json);
        }

        var world = fixture.World;
        var inspect = await EngineWorld.DockerAsync("inspect", world.EngineName);
        recorder.Add("docker inspect of the engine", inspect.StdOut + inspect.StdErr);
        recorder.Add("docker command lines of the tool", string.Join('\n', scenarios.Commands));
        recorder.Add("manifests left in the pool", await new BranchKit(fixture.Engine, output).InPoolAsync("for f in /pool/images/*/manifest.json; do cat \"$f\"; done"));
        recorder.Add("status", world.RunCli("status", "--format", "json").Out);

        // The check is not vacuous: the recording holds the URLs, the credential-bearing commands and large archives.
        Assert.Contains(recorder.Items, i => i.Text.Contains(fixture.S3Url("nightly/"), StringComparison.Ordinal));
        Assert.Contains(recorder.Items, i => i.Label.StartsWith("docker command lines", StringComparison.Ordinal) && i.Text.Contains("exec", StringComparison.Ordinal));
        Assert.Contains(recorder.Items, i => i.Label.Contains("(bytes)", StringComparison.Ordinal));
        var secrets = new[] { fixture.S3KeyId, fixture.S3Secret, fixture.WrongS3Secret, fixture.SasToken, fixture.SasSignature, Uri.UnescapeDataString(fixture.SasSignature), fixture.WrongSasToken };
        foreach (var secret in secrets.Where(s => s.Length > 0))
        {
            foreach (var (label, text) in recorder.Items)
            {
                Assert.False(text.Contains(secret, StringComparison.Ordinal), $"A storage secret is in: {label}");
            }
        }

        output.WriteLine($"{recorder.Items.Count} outputs and files were searched, among them {scenarios.Commands.Count} docker command lines.");
    }
}
