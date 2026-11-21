using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>
/// Tasks 5.1 to 5.3: URL builds against a MinIO server of the test's own, in whatever clone mode the pool has (thin on the
/// default pool, copy with <c>SQLBRANCH_IT_POOL_BACKEND=plain</c>). The cases are in <see cref="MinioScenarios"/>.
/// </summary>
[Collection(CloudSourceGroup.Name)]
[Trait("Category", "Integration")]
public sealed class MinioSourceIntegrationTests(CloudSourceFixture fixture, ITestOutputHelper output)
{
    private MinioScenarios Scenarios(OutputRecorder? recorder = null) => new(fixture, output, recorder ?? new OutputRecorder());

    [IntegrationFact]
    public void The_fixture_started_the_engine_and_the_storage_servers()
    {
        Assert.True(fixture.Engine.Init.Exit == 0, fixture.Engine.Init.Out + fixture.Engine.Init.Err);
        Assert.NotNull(fixture.Minio);
        Assert.NotNull(fixture.UntrustedMinio);
        Assert.NotNull(fixture.Azurite);
        output.WriteLine("pool clone mode is reported by the builds below");
    }

    [IntegrationFact]
    public async Task A_url_build_lists_the_image_leaves_no_backup_or_credential_and_records_the_url() =>
        await Scenarios().BuildFromUrlAsync(json: true);

    [IntegrationFact]
    public async Task A_post_build_script_sees_no_credential_of_the_build() =>
        await Scenarios().PostScriptSeesNoCredentialAsync(json: true);

    [IntegrationFact]
    public async Task A_wrong_secret_is_backup_unreachable_and_leaves_no_image_or_credential() =>
        await Scenarios().WrongSecretAsync(json: true);

    [IntegrationFact]
    public async Task A_server_with_an_unknown_certificate_authority_is_backup_unreachable() =>
        await Scenarios().UntrustedCertificateAsync(json: true);

    [IntegrationFact]
    public async Task Two_builds_from_one_folder_at_the_same_moment_both_succeed() =>
        await Scenarios().ParallelBuildsAsync(json: true);

    [IntegrationFact]
    public async Task A_killed_build_leaves_a_credential_that_the_next_build_removes_and_app_cred_stays() =>
        await Scenarios().KilledBuildCredentialIsRemovedByTheNextBuildAsync(json: true);
}
