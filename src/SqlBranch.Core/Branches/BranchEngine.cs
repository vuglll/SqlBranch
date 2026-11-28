using System.Globalization;
using System.Text;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Engine;
using SqlBranch.Core.Images;
using SqlBranch.Core.Output;
using SqlBranch.Core.Pools;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.Branches;

/// <summary>
/// What <c>branch conn</c>, <c>reset</c> and <c>rm</c> share: an open session as <c>sa</c>, the shell of the engine
/// container, the check that a branch folder with a <c>branch.json</c> exists (design D5: they never act on a database
/// without it), and the reading and writing of that record.
/// </summary>
internal sealed class BranchEngine : IAsyncDisposable
{
    private const string RecordTag = "SQLBRANCH_RECORD=";
    private const string ManifestTag = "SQLBRANCH_MANIFEST=";

    private readonly IContainerRuntime _runtime;
    private readonly string _engineName;
    private readonly string _engineImage;
    private readonly PoolLocation _pool;

    private BranchEngine(IContainerRuntime runtime, string engineName, string engineImage, PoolLocation pool, string password, EngineEndpoint endpoint, IEngineSession session)
    {
        _engineImage = engineImage;
        _pool = pool;
        _runtime = runtime;
        _engineName = engineName;
        Password = password;
        Endpoint = endpoint;
        Session = session;
    }

    /// <summary>The pool format read with the credentials: 1 or 2.</summary>
    public int PoolFormat { get; private set; } = 1;

    private CredentialsFile Credentials { get; set; } = null!;

    internal Task RequireWritableAsync(CancellationToken cancellationToken) =>
        PoolUpgradeGuard.RequireWritableAsync(Credentials, Session, cancellationToken);

    /// <summary>
    /// Reads the pool format again after the command's locks (design D8). During an unfinished or running upgrade it exits 3
    /// <c>pool_upgrade_required</c>; otherwise the command goes on with the format it has now.
    /// </summary>
    internal async Task RecheckFormatAsync(CancellationToken cancellationToken)
    {
        Credentials = await PoolUpgradeGuard.RecheckAsync(_runtime, _engineName, _engineImage, _pool, Session, cancellationToken).ConfigureAwait(false);
        PoolFormat = Credentials.Format;
    }

    public IContainerRuntime Runtime => _runtime;

    public string EngineName => _engineName;

    public string Password { get; }

    public EngineEndpoint Endpoint { get; }

    public IEngineSession Session { get; }

    /// <summary>The engine's data source as a client writes it, for example <c>127.0.0.1,14330</c>.</summary>
    public string Server => $"{Endpoint.Host},{Endpoint.Port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Finds the running engine, reads the password from the pool's credentials file (never from container output) and
    /// opens a session. The name has been checked by the caller already, so nothing here runs for a bad name.
    /// </summary>
    public static async Task<BranchEngine> OpenAsync(
        IContainerRuntime runtime, IEngineConnector connector, SecretRedactor redactor, string engineName, FoundPool pool, int fallbackPort, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pool);
        var engine = ImageCatalog.RequireRunningEngine(pool);
        var credentials = await EngineCredentials.ReadFileAsync(runtime, engine.Image, pool.Location, cancellationToken).ConfigureAwait(false)
            ?? throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"The engine credentials are missing from {pool.Location}. Run 'sqlbranch teardown --force' and 'sqlbranch init' to start again.");
        var password = credentials.Password;
        redactor.Register(password);

        var endpoint = EngineEndpoint.Loopback(EngineStarter.PortOf(engine, fallbackPort));
        var session = await connector.ConnectAsync(endpoint, password, cancellationToken).ConfigureAwait(false);
        return new BranchEngine(runtime, engineName, engine.Image, pool.Location, password, endpoint, session) { PoolFormat = credentials.Format, Credentials = credentials };
    }

    public ValueTask DisposeAsync() => Session.DisposeAsync();

    /// <summary>Takes the lock of the branch name for the rest of the session, or exits 1 when another command holds it.</summary>
    public async Task TakeBranchLockAsync(string name, CancellationToken cancellationToken)
    {
        if (!await EngineLocks.TryTakeAsync(Session, EngineLocks.Branch(name), cancellationToken).ConfigureAwait(false))
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed,
                $"Another command is creating, resetting or removing the branch '{name}'. Wait for it to finish, then run the command again.");
        }
    }

    /// <summary>
    /// The record of the branch, or exit 1 <c>branch_not_found</c> when the pool has no <c>branch.json</c> for it. A
    /// database without one (a system database, an image build, a database a user made) is not a branch and is never touched.
    /// The record is null when the file is there but cannot be read.
    /// </summary>
    public async Task<(bool Exists, BranchRecord? Record)> ReadRecordAsync(string name, CancellationToken cancellationToken)
    {
        var json = Quoting.Shell(BranchPaths.Json(name));
        var output = await ShellAsync($"if [ -f {json} ]; then echo {RecordTag}$(base64 -w0 {json}); else echo {RecordTag}none; fi", "reading branch.json", cancellationToken).ConfigureAwait(false);
        var value = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.StartsWith(RecordTag, StringComparison.Ordinal))?[RecordTag.Length..];
        if (value is null or "none")
        {
            return (false, null);
        }

        try
        {
            return (true, BranchRecord.FromJson(Encoding.UTF8.GetString(Convert.FromBase64String(value))));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
        {
            return (true, null);
        }
    }

    /// <summary>The manifest of an image, or exit 1 <c>image_not_found</c> with <paramref name="whenMissing"/> as the message.</summary>
    public async Task<ImageManifest> ReadManifestAsync(string image, string whenMissing, CancellationToken cancellationToken)
    {
        var path = Quoting.Shell(ImagePaths.Manifest(image));
        var output = await ShellAsync($"if [ -f {path} ]; then echo {ManifestTag}$(base64 -w0 {path}); else echo {ManifestTag}none; fi", "reading the image", cancellationToken).ConfigureAwait(false);
        var value = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.StartsWith(ManifestTag, StringComparison.Ordinal))?[ManifestTag.Length..];
        if (value is null or "none")
        {
            throw new SqlBranchException(ErrorCodes.ImageNotFound, whenMissing);
        }

        try
        {
            return ImageManifest.FromJson(Encoding.UTF8.GetString(Convert.FromBase64String(value)));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
        {
            throw new SqlBranchException(ErrorCodes.OperationFailed, $"The manifest of the image '{image}' cannot be read, so no branch can be made from it.");
        }
    }

    /// <summary>Exit 1 <c>branch_not_found</c> naming the name, for a database that is not a branch as well as for an unknown name.</summary>
    public static SqlBranchException NotABranch(string name) =>
        new(ErrorCodes.BranchNotFound,
            $"There is no branch named '{name}': {BranchPaths.Json(name)} does not exist, so '{name}' is not a branch SqlBranch created and it is left alone. 'sqlbranch branch list' shows the branches.");

    /// <summary>Writes <c>branch.json</c>.</summary>
    public async Task WriteRecordAsync(string name, BranchRecord record, CancellationToken cancellationToken)
    {
        await ShellAsync(RecordWriteScript(name, record), "writing branch.json", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The script that writes <c>branch.json</c> to <c>branch.json.new</c> and renames it over the record (design D11).</summary>
    public static string RecordWriteScript(string name, BranchRecord record)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(record.ToJson()));
        var temporary = Quoting.Shell(BranchPaths.Json(name) + ".new");
        return $"printf %s {Quoting.Shell(encoded)} | base64 -d > {temporary} && sync {temporary} && mv -f {temporary} {Quoting.Shell(BranchPaths.Json(name))}";
    }

    /// <summary>The engine has a database of this name.</summary>
    public async Task<bool> DatabaseExistsAsync(string name, CancellationToken cancellationToken) =>
        Convert.ToInt32(
            await Session.ScalarAsync("SELECT COUNT(*) FROM sys.databases WHERE name = @n", new Dictionary<string, object?> { ["n"] = name }, cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) > 0;

    /// <summary>
    /// The <c>state_desc</c> of the database of this name when it has a file under the branch's folder, so it is the branch's own and may be dropped;
    /// null when there is no such database or it is somebody else's.
    /// </summary>
    public async Task<string?> OwnedStateAsync(string name, CancellationToken cancellationToken) =>
        StateText(
            await Session.ScalarAsync(
                "SELECT TOP (1) d.state_desc FROM sys.databases AS d WHERE d.name = @n AND EXISTS " +
                "(SELECT 1 FROM sys.master_files AS f WHERE f.database_id = d.database_id AND LEFT(f.physical_name, LEN(@p)) = @p)",
                new Dictionary<string, object?> { ["n"] = name, ["p"] = BranchPaths.Branch(name) + "/" }, cancellationToken).ConfigureAwait(false));

    // Convert.ToString turns null into "", which would read as a database in an unknown state.
    private static string? StateText(object? value) => value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

    /// <summary>The <c>state_desc</c> of the database of this name, or null when the engine has none.</summary>
    public async Task<string?> DatabaseStateAsync(string name, CancellationToken cancellationToken) =>
        StateText(
            await Session.ScalarAsync("SELECT state_desc FROM sys.databases WHERE name = @n", new Dictionary<string, object?> { ["n"] = name }, cancellationToken).ConfigureAwait(false));

    /// <summary>The state of a database that is up: only then can connections be closed with SINGLE_USER.</summary>
    public static bool IsOnline(string? state) => string.Equals(state, "ONLINE", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Closes every open connection to an ONLINE database (single-user, rolling back what is open) and drops it, which deletes its files.
    /// A database that is not ONLINE (offline, recovery pending, suspect) cannot be altered, so it is dropped directly.
    /// </summary>
    public async Task DropAsync(string name, string? state, CancellationToken cancellationToken)
    {
        var quoted = Quoting.Identifier(name);
        var online = IsOnline(state);
        if (online)
        {
            await Session.ExecuteAsync($"ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;", cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await Session.ExecuteAsync($"DROP DATABASE {quoted};", cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The database stays as it was: give the connections back, then report the drop's own failure.
        catch (Exception)
#pragma warning restore CA1031
        {
            if (online)
            {
                await TryMultiUserAsync(quoted).ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task TryMultiUserAsync(string quoted)
    {
        try
        {
            await Session.ExecuteAsync($"ALTER DATABASE {quoted} SET MULTI_USER;", CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best effort only; the original failure is what the user needs to see.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>Runs a script in the engine container and returns its output, or throws exit 1 with the first line of its error.</summary>
    public async Task<string> ShellAsync(string script, string what, CancellationToken cancellationToken)
    {
        var result = await _runtime.ExecAsync(_engineName, ["sh", "-c", script], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new SqlBranchException($"The engine could not do this step ({what}), {Detail(result)}.");
        }

        return result.StdOut;
    }

    /// <summary>Runs a script and returns the result without judging it.</summary>
    public Task<ContainerResult> TryShellAsync(string script, CancellationToken cancellationToken) =>
        _runtime.ExecAsync(_engineName, ["sh", "-c", script], cancellationToken);

    public static string Detail(ContainerResult result)
    {
        var line = result.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return $"exit code {result.ExitCode.ToString(CultureInfo.InvariantCulture)}{(line is null ? string.Empty : ": " + line)}";
    }
}
