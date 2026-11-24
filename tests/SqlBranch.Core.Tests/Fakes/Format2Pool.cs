using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;

namespace SqlBranch.Core.Tests.Fakes;

/// <summary>
/// Writes the format-2 layout directly (design D2, D3), for tests that need a format-2 pool before
/// <c>pool upgrade</c> exists. After that command exists the same pools can also be built with it.
/// </summary>
internal static class Format2Pool
{
    public const string Password = CliWorld.StoredPassword;

    /// <summary>The credentials file of a format-2 pool; <paramref name="state"/> null means no upgrade record.</summary>
    public static string Credentials(string? state = UpgradeRecord.Done, string password = Password) =>
        CredentialsFile.Format2(password, state is null ? null : new UpgradeRecord(new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero), state)).ToJson();

    /// <summary>Makes the pool of <paramref name="world"/> a format-2 pool.</summary>
    public static void Apply(CliWorld world, string? state = UpgradeRecord.Done) => world.Host.CredentialsJson = Credentials(state);

    public static string VersionId(string name, string stamp = "20261201T090000000Z") => $"{name}@{stamp}";

    /// <summary>The catalog of a name with one version in the legacy files at the top of the image folder.</summary>
    public static string Catalog(string name, string? version) =>
        new ImageCatalogFile(name, version, version is null ? [] : [new ImageVersionRef(version, ".")]).ToJson();
}
