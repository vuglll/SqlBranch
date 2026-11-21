using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>
/// Task 5.3: every case of task 5.2 in JSON and in text, then everything it printed or left behind is searched for the access
/// key ID and the secrets: standard output and error, the engine container's settings, every manifest and the build folders.
/// </summary>
[Collection(CloudSourceGroup.Name)]
[Trait("Category", "Integration")]
public sealed class MinioSecretLeakIntegrationTests(CloudSourceFixture fixture, ITestOutputHelper output)
{
    [IntegrationFact]
    public async Task No_storage_secret_is_in_any_output_manifest_build_folder_or_the_engine_settings()
    {
        var recorder = new OutputRecorder();
        var scenarios = new MinioScenarios(fixture, output, recorder);
        foreach (var json in new[] { true, false })
        {
            await scenarios.BuildFromUrlAsync(json);
            await scenarios.PostScriptSeesNoCredentialAsync(json);
            await scenarios.WrongSecretAsync(json);
            await scenarios.UntrustedCertificateAsync(json);
            await scenarios.ParallelBuildsAsync(json);
            await scenarios.KilledBuildCredentialIsRemovedByTheNextBuildAsync(json);
        }

        var world = fixture.World;
        var inspect = await EngineWorld.DockerAsync("inspect", world.EngineName);
        recorder.Add("docker inspect of the engine", inspect.StdOut + inspect.StdErr);
        recorder.Add("manifests left in the pool", await new BranchKit(fixture.Engine, output).InPoolAsync("for f in /pool/images/*/manifest.json; do cat \"$f\"; done"));
        recorder.Add("build folders left in the pool", await new BranchKit(fixture.Engine, output).InPoolAsync("for f in /pool/build/*/*.json; do cat \"$f\"; done 2>/dev/null"));
        recorder.Add("status", world.RunCli("status", "--format", "json").Out);

        // The check is not vacuous: the recording is large and holds the secrets' neighbours, the URL and the credential name.
        Assert.Contains(recorder.Items, i => i.Text.Contains(fixture.S3CredentialName, StringComparison.Ordinal));
        foreach (var secret in new[] { fixture.S3KeyId, fixture.S3Secret, fixture.WrongS3Secret })
        {
            foreach (var (label, text) in recorder.Items)
            {
                Assert.False(text.Contains(secret, StringComparison.Ordinal), $"A storage secret is in: {label}");
            }
        }

        output.WriteLine($"{recorder.Items.Count} outputs and files were searched.");
    }
}
