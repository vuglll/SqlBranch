using Xunit.Abstractions;

namespace SqlBranch.IntegrationTests;

/// <summary>
/// Task 5.1: export to a file and import it again on a real engine, in whatever clone mode the pool has (thin on the default managed
/// pool, copy with <c>SQLBRANCH_IT_POOL_BACKEND=plain</c>). The cases are in <see cref="ImageArchiveScenarios"/>.
/// </summary>
[Collection(CloudSourceGroup.Name)]
[Trait("Category", "Integration")]
public sealed class ImageFileArchiveIntegrationTests(CloudSourceFixture fixture, ITestOutputHelper output)
{
    private ImageArchiveScenarios Scenarios() => new(fixture, output, new OutputRecorder());

    [IntegrationFact]
    public async Task An_image_exports_to_a_file_and_imports_with_equal_files_manifest_and_a_branch_that_answers() =>
        await Scenarios().FileRoundTripAsync(json: true);

    [IntegrationFact]
    public async Task The_text_output_of_a_file_round_trip_reports_the_clone_mode() =>
        await Scenarios().FileRoundTripAsync(json: false);

    [IntegrationFact]
    public async Task An_existing_destination_is_destination_exists_without_force_and_replaced_with_it() =>
        await Scenarios().ExistingDestinationAsync(json: true);

    [IntegrationFact]
    public async Task A_changed_byte_and_a_cut_archive_are_archive_corrupt_and_leave_no_image_and_a_clean_status() =>
        await Scenarios().CorruptAndTruncatedAsync(json: true);

    [IntegrationFact]
    public async Task An_image_that_does_not_fit_is_pool_full_before_anything_is_written() =>
        await Scenarios().NotEnoughSpaceAsync(json: true);

    [IntegrationFact]
    public async Task A_file_removed_from_the_image_while_it_is_exported_fails_the_export_and_leaves_no_destination() =>
        await Scenarios().ImageRemovedDuringExportAsync(json: true);

    [IntegrationThinFact]
    public async Task Ctrl_C_during_an_import_leaves_no_image_and_no_build_folder() =>
        await Scenarios().CancelledImportAsync(json: true);

    [IntegrationFact]
    public async Task A_killed_import_is_listed_by_status_as_a_build_folder_and_no_image_is_listed() =>
        await Scenarios().KilledImportAsync(json: true);
}
