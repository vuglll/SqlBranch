using System.Globalization;

namespace SqlBranch.Core.Engine;

/// <summary>
/// The application locks that tell a running build, create or reset from a dead
/// one (design D7). They are taken with <c>sp_getapplock</c> in <c>master</c>,
/// as exclusive session locks, so a client that dies releases them. Image
/// builds and branch commands hold the lock for their name for as long as they
/// run; a build also holds the lock of its build id, which is what lets
/// <c>status</c> tie a <c>/pool/build/&lt;id&gt;/</c> folder to a build.
/// </summary>
public static class EngineLocks
{
    /// <summary>Held by a build of the image <paramref name="name"/>.</summary>
    public static string Image(string name) => "sqlbranch:image:" + name;

    /// <summary>Held by a build while <c>/pool/build/&lt;id&gt;/</c> and <c>sqlbranch_build_&lt;id&gt;</c> exist.</summary>
    public static string Build(string id) => "sqlbranch:build:" + id;

    /// <summary>Held by a URL build for as long as its storage credential <paramref name="credentialName"/> may exist.</summary>
    public static string Credential(string credentialName) =>
        "sqlbranch_cred_" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(credentialName)))[..16];

    /// <summary>Held by <c>pool upgrade</c> for as long as it runs. <c>status</c> tests it and never takes it.</summary>
    public const string PoolUpgrade = "sqlbranch:pool-upgrade";

    /// <summary>Held by <c>branch create</c>, <c>reset</c> and <c>rm</c> of the branch <paramref name="name"/>.</summary>
    public static string Branch(string name) => "sqlbranch:branch:" + name;

    /// <summary>
    /// The statement that tests a lock without keeping it: it takes the lock with
    /// no wait, gives it back, and returns the result of <c>sp_getapplock</c>.
    /// The resource name is the parameter <c>@resource</c>, never part of the text.
    /// </summary>
    public const string TestSql =
        "USE master; DECLARE @r int; " +
        "EXEC @r = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; " +
        "IF @r >= 0 EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = 'Session'; SELECT @r;";

    /// <summary>
    /// The statement that takes a lock for the rest of the session, without waiting. It runs in <c>master</c>,
    /// the database <see cref="TestSql"/> looks in, and returns the result of <c>sp_getapplock</c>.
    /// </summary>
    public const string TakeSql =
        "USE master; DECLARE @r int; " +
        "EXEC @r = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;";

    /// <summary>
    /// Like <see cref="TakeSql"/> but shared: any number of sessions can hold it together, and none can take it
    /// exclusively while one does. <c>branch create</c> holds its image this way, so creates from one image run side
    /// by side while <c>image rm</c> and <c>image build</c>, which take the lock exclusively, wait for them.
    /// </summary>
    public const string TakeSharedSql =
        "USE master; DECLARE @r int; " +
        "EXEC @r = sys.sp_getapplock @Resource = @resource, @LockMode = 'Shared', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;";

    /// <summary>Gives back a lock this session took with <see cref="TakeSql"/>.</summary>
    public const string ReleaseSql =
        "USE master; DECLARE @r int; EXEC @r = sys.sp_releaseapplock @Resource = @resource, @LockOwner = 'Session'; SELECT @r;";

    /// <summary>
    /// Tests a lock with <c>APPLOCK_TEST</c>, which never takes it: 1 when an exclusive session lock could be granted,
    /// 0 when another session holds it. It runs in <c>master</c>, where the locks are taken.
    /// </summary>
    public const string ProbeSql = "USE master; SELECT APPLOCK_TEST('public', @resource, 'Exclusive', 'Session');";

    /// <summary>The parameter name of the resource in <see cref="TestSql"/> and <see cref="TakeSql"/>.</summary>
    public const string ResourceParameter = "resource";

    /// <summary>True when another session holds the lock.</summary>
    public static async Task<bool> IsHeldAsync(IEngineSession session, string resource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var result = Convert.ToInt32(await session.ScalarAsync(TestSql, new Dictionary<string, object?> { [ResourceParameter] = resource }, cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return result < 0;
    }

    /// <summary>True when another session holds the lock. The lock is only looked at, never taken, not even for a moment.</summary>
    public static async Task<bool> IsHeldWithoutTakingAsync(IEngineSession session, string resource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var result = Convert.ToInt32(await session.ScalarAsync(ProbeSql, new Dictionary<string, object?> { [ResourceParameter] = resource }, cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return result == 0;
    }

    /// <summary>
    /// Takes the lock for the rest of the session and returns true, or returns false when another session holds it.
    /// The lock is released when the session ends, so a client that dies releases it.
    /// </summary>
    public static async Task<bool> TryTakeAsync(IEngineSession session, string resource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var result = Convert.ToInt32(await session.ScalarAsync(TakeSql, new Dictionary<string, object?> { [ResourceParameter] = resource }, cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return result >= 0;
    }

    /// <summary>
    /// Takes the lock in shared mode for the rest of the session and returns true, or returns false when another
    /// session holds it exclusively.
    /// </summary>
    public static async Task<bool> TryTakeSharedAsync(IEngineSession session, string resource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var result = Convert.ToInt32(await session.ScalarAsync(TakeSharedSql, new Dictionary<string, object?> { [ResourceParameter] = resource }, cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return result >= 0;
    }

    /// <summary>Gives back a lock this session holds.</summary>
    public static async Task ReleaseAsync(IEngineSession session, string resource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        await session.ScalarAsync(ReleaseSql, new Dictionary<string, object?> { [ResourceParameter] = resource }, cancellationToken).ConfigureAwait(false);
    }
}
