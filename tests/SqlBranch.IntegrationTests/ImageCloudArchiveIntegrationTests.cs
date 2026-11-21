using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>
/// Task 5.2: export and import through F1's MinIO and Azurite fixtures. The Core storage clients run on the client with an HTTP handler
/// that reaches the fixture's published port and trusts only the fixture's test certificate authority (design D9). The cases are in
/// <see cref="ImageArchiveScenarios"/>.
/// </summary>
[Collection(CloudSourceGroup.Name)]
[Trait("Category", "Integration")]
public sealed class ImageCloudArchiveIntegrationTests(CloudSourceFixture fixture, ITestOutputHelper output)
{
    private ImageArchiveScenarios Scenarios() => new(fixture, output, new OutputRecorder());

    [IntegrationFact]
    public async Task An_image_round_trips_through_minio_and_an_existing_object_is_kept_without_force() =>
        await Scenarios().S3RoundTripAsync(json: true);

    [IntegrationFact]
    public async Task A_cancelled_upload_to_minio_leaves_no_object_and_no_unfinished_multipart_upload() =>
        await Scenarios().S3CancelledUploadAsync(json: true);

    [IntegrationFact]
    public async Task An_object_another_client_creates_during_the_upload_is_kept_because_minio_honours_if_none_match() =>
        await Scenarios().S3ObjectCreatedDuringUploadAsync(json: true);

    [IntegrationFact]
    public async Task A_wrong_secret_on_an_s3_import_is_storage_unreachable_and_the_pool_is_unchanged() =>
        await Scenarios().S3WrongSecretAsync(json: true);

    [IntegrationFact]
    public async Task An_image_round_trips_through_azurite_and_an_existing_blob_is_kept_without_force() =>
        await Scenarios().BlobRoundTripAsync(json: true);

    [IntegrationFact]
    public async Task A_wrong_sas_signature_on_a_blob_import_is_storage_unreachable_and_the_pool_is_unchanged() =>
        await Scenarios().BlobWrongSasAsync(json: true);
}
