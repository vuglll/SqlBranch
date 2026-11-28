using System.Globalization;
using SqlBranch.Core.Containers;
using SqlBranch.Core.Pools;

namespace SqlBranch.Core.Tests.Fakes;

/// <summary>
/// A container host for <c>init</c> tests: the pool directory, the managed pool
/// file and mount, and the engine folder with its credentials, answered from the
/// scripts that work containers and helpers are given.
/// </summary>
internal sealed class FakeInitHost
{
    public const string PoolDir = "/data/sb";

    public FakeInitHost(FakeContainerRuntime runtime, FakeEngineConnector connector)
    {
        Runtime = runtime;
        Connector = connector;
        runtime.RunHandler = Handle;
        runtime.ExecHandler = (_, command) => Exec(command);
    }

    /// <summary>What the engine's pool holds, as the status listing script would find it.</summary>
    public List<(string Name, bool BranchJson, bool Next)> BranchFolders { get; } = [];

    public Dictionary<string, string> BranchRecords { get; } = [];

    public List<string> ImageFolders { get; } = [];

    public List<string> RemovingFolders { get; } = [];

    /// <summary>The temporary files of a pool upgrade that the status listing finds, such as <c>credentials.json.new</c>.</summary>
    public List<string> UpgradeTemporaryFiles { get; } = [];

    public List<string> BuildFolders { get; } = [];

    /// <summary>The <c>credential.json</c> of build folders, by build id.</summary>
    public Dictionary<string, string> CredentialMarkers { get; } = [];

    /// <summary>Total, used and free bytes that <c>df</c> reports for the pool.</summary>
    public (long Total, long Used, long Free) Space { get; set; } = (100_000_000_000, 40_000_000_000, 60_000_000_000);

    /// <summary>Whether a file can be written to the pool.</summary>
    public bool PoolWritable { get; set; } = true;

    /// <summary>What the clone probe reports inside the engine.</summary>
    public string EngineProbe { get; set; } = "thin";

    private ContainerResult Exec(IReadOnlyList<string> command)
    {
        var script = command.Count >= 3 ? command[2] : string.Empty;
        if (script == SqlBranch.Core.Engine.StatusReporter.ListingScript)
        {
            var lines = new List<string>();
            lines.AddRange(BranchFolders.Select(b => $"SQLBRANCH_BRANCH|{b.Name}|{(b.BranchJson ? 1 : 0)}|{(b.Next ? 1 : 0)}|{BranchRecords.GetValueOrDefault(b.Name)}"));
            lines.AddRange(ImageFolders.Select(i => $"SQLBRANCH_IMAGE|{i}"));
            lines.AddRange(RemovingFolders.Select(r => $"SQLBRANCH_REMOVING|{r}"));
            lines.AddRange(UpgradeTemporaryFiles.Select(f => $"SQLBRANCH_UPGRADETEMP|{f}"));
            lines.AddRange(BuildFolders.Select(b => $"SQLBRANCH_BUILD|{b}"));
            lines.AddRange(CredentialMarkers.Select(m => $"SQLBRANCH_CRED|{m.Key}|{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(m.Value))}"));
            lines.Add(SpaceLine());
            return FakeContainerRuntime.Ok(string.Join('\n', lines));
        }

        if (script.Contains("SQLBRANCH_POOLFORMAT=", StringComparison.Ordinal))
        {
            RereadCount++;
            return FakeContainerRuntime.Ok("SQLBRANCH_POOLFORMAT=" +
                (CredentialsJson is null ? "none" : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(CredentialsJson))));
        }

        return FakeContainerRuntime.Ok("SQLBRANCH_PROBE=" + EngineProbe);
    }

    /// <summary>How many times the exec re-read of the credentials ran (design D8).</summary>
    public int RereadCount { get; private set; }

    private string SpaceLine() =>
        string.Create(CultureInfo.InvariantCulture, $"SQLBRANCH_SPACE|{Space.Total} {Space.Used} {Space.Free}");

    public FakeContainerRuntime Runtime { get; }

    public FakeEngineConnector Connector { get; }

    public bool DirectoryReflink { get; set; }

    public bool PoolFileExists { get; set; }

    public bool Mounted { get; set; }

    public bool FailMount { get; set; }

    public bool MarkerExists { get; set; }

    public bool EngineDirExists { get; set; }

    /// <summary>Every JSON a format-2 credentials write was given, in order, whether or not it succeeded.</summary>
    public List<string> CredentialsRewrites { get; } = [];

    /// <summary>The error text the format-2 credentials write fails with (for example "No space left on device"); null lets it succeed.</summary>
    public string? CredentialsRewriteFailure { get; set; }

    /// <summary>The JSON of the credentials file, or null.</summary>
    public string? CredentialsJson { get; set; }

    public bool VolumeInUse { get; set; }

    /// <summary>A step to fail: <c>enginedir</c>, <c>engine</c> or <c>credentials</c>.</summary>
    public string? FailStep { get; set; }

    public long HostFree { get; set; } = 100_000_000_000;

    public IEnumerable<string> Scripts => Runtime.Runs.Select(FakeContainerRuntime.ScriptOf);

    private ContainerResult Handle(ContainerSpec spec)
    {
        var script = FakeContainerRuntime.ScriptOf(spec);
        if (spec.Image == ManagedPool.HelperImage)
        {
            return Helper(script);
        }

        if (spec.Detach)
        {
            if (FailStep == "engine")
            {
                return FakeContainerRuntime.Fail(125, "port is already allocated");
            }

            // A new engine over existing system databases keeps the password they have.
            if (CredentialsJson is null || !EngineDirExists)
            {
                Connector.SaPassword = spec.Env["MSSQL_SA_PASSWORD"];
            }

            return FakeContainerRuntime.Ok("container-id");
        }

        var source = spec.Mounts[0].Source;
        if (script.Contains("SQLBRANCH_DATAFILE", StringComparison.Ordinal))
        {
            return FakeContainerRuntime.Ok(string.Join('\n', DataFiles.Select(f => "SQLBRANCH_DATAFILE|" + f)));
        }

        if (script.Contains("SQLBRANCH_TEARDOWN", StringComparison.Ordinal))
        {
            FolderRuns.Add(script);
            if (FailFolders)
            {
                return FakeContainerRuntime.Fail(1, "rm: cannot remove: Device or resource busy");
            }

            var imports = string.Join('\n', ImportFiles.Select(f => "SQLBRANCH_IMPORT|" + f));
            ImportFiles.Clear();
            DataFiles.Clear();
            EngineDirExists = false;
            CredentialsJson = null;
            MarkerExists = false;
            BranchesDirExists = false;
            return FakeContainerRuntime.Ok(imports + "\nSQLBRANCH_TEARDOWN=done");
        }

        if (script.Contains("SQLBRANCH_POOL=", StringComparison.Ordinal))
        {
            return FakeContainerRuntime.Ok("SQLBRANCH_POOL=" + (MarkerExists ? "marked" : "empty"));
        }

        if (script.Contains("SQLBRANCH_WRITABLE", StringComparison.Ordinal))
        {
            return FakeContainerRuntime.Ok($"SQLBRANCH_WRITABLE={(PoolWritable ? "yes" : "no")}\n{SpaceLine()}");
        }

        if (script.Contains("SQLBRANCH_PROBE", StringComparison.Ordinal))
        {
            var thin = source == ManagedPaths.Default.MountPoint || (source == PoolDir && DirectoryReflink);
            return FakeContainerRuntime.Ok("SQLBRANCH_PROBE=" + (thin ? "thin" : "copy"));
        }

        if (script.Contains("SQLBRANCH_MARKER=new", StringComparison.Ordinal))
        {
            var isNew = !MarkerExists;
            MarkerExists = true;
            return FakeContainerRuntime.Ok(isNew ? "SQLBRANCH_MARKER=new" : string.Empty);
        }

        if (script.Contains("SQLBRANCH_ENGINEDIR", StringComparison.Ordinal))
        {
            if (FailStep == "enginedir")
            {
                return FakeContainerRuntime.Fail(1, "mkdir: read-only file system");
            }

            var existed = EngineDirExists;
            EngineDirExists = true;
            return FakeContainerRuntime.Ok("SQLBRANCH_ENGINEDIR=" + (existed ? "existing" : "new"));
        }

        if (script.Contains("SQLBRANCH_CREDENTIALS=", StringComparison.Ordinal))
        {
            return CredentialsReply();
        }

        if (spec.Stdin is { } rewritten && script.Contains("credentials.json.new", StringComparison.Ordinal) && script.Contains("mv -f", StringComparison.Ordinal))
        {
            // The format-2 write (design D2): the old file stays when a step before the rename fails.
            CredentialsRewrites.Add(rewritten);
            if (CredentialsRewriteFailure is { } failure)
            {
                return FakeContainerRuntime.Fail(1, failure);
            }

            CredentialsJson = rewritten;
            return FakeContainerRuntime.Ok();
        }

        if (spec.Stdin is { } json && script.Contains("cat > /pool/engine/credentials.json", StringComparison.Ordinal))
        {
            if (FailStep == "credentials")
            {
                return FakeContainerRuntime.Fail(1, "no space left on device");
            }

            CredentialsJson = json;
            return FakeContainerRuntime.Ok();
        }

        if (script.Contains("rm -f /pool/engine/credentials.json", StringComparison.Ordinal))
        {
            CredentialsJson = null;
        }

        if (script.Contains("rm -rf /pool/engine", StringComparison.Ordinal))
        {
            EngineDirExists = false;
            CredentialsJson = null;
        }

        if (script.Contains("rmdir /pool/branches", StringComparison.Ordinal))
        {
            BranchesDirExists = false;
        }

        if (script.Contains("rm -f /pool/.sqlbranch-pool", StringComparison.Ordinal))
        {
            MarkerExists = false;
        }

        return FakeContainerRuntime.Ok();
    }

    /// <summary>What the read script prints: the file as base64, or <c>none</c>.</summary>
    public ContainerResult CredentialsReply() =>
        FakeContainerRuntime.Ok("SQLBRANCH_CREDENTIALS=" +
            (CredentialsJson is null ? "none" : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(CredentialsJson))));

    /// <summary>Names the data-file listing prints, as in <c>/pool/engine/data</c>.</summary>
    public List<string> DataFiles { get; } = [];

    /// <summary>Backups in <c>/pool/import</c>.</summary>
    public List<string> ImportFiles { get; } = [];

    /// <summary>Every folder-deleting script that ran.</summary>
    public List<string> FolderRuns { get; } = [];

    /// <summary>The folder-deleting work container fails.</summary>
    public bool FailFolders { get; set; }

    /// <summary>Unmounting the managed pool fails, as when the loop device is busy.</summary>
    public bool FailUnmount { get; set; }

    /// <summary>Deleting the pool file fails.</summary>
    public bool FailDelete { get; set; }

    /// <summary>Whether the pool takes the copy-on-write hint.</summary>
    public bool CowHintSupported { get; set; } = true;

    /// <summary>Whether <c>/pool/branches</c> exists.</summary>
    public bool BranchesDirExists { get; set; }

    /// <summary>Whether the hint is set on <c>/pool/branches</c>.</summary>
    public bool CowHintSet { get; set; }

    private ContainerResult Helper(string script)
    {
        if (script.Contains("SQLBRANCH_COWHINT", StringComparison.Ordinal))
        {
            var isNew = !BranchesDirExists;
            BranchesDirExists = true;
            CowHintSet = CowHintSupported;
            return FakeContainerRuntime.Ok($"SQLBRANCH_BRANCHES={(isNew ? "new" : "existing")}\nSQLBRANCH_COWHINT={(CowHintSupported ? "set" : "unsupported")}");
        }

        if (script.Contains("df -B1", StringComparison.Ordinal))
        {
            return FakeContainerRuntime.Ok(HostFree.ToString(CultureInfo.InvariantCulture));
        }

        if (script.Contains("SQLBRANCH_WRITABLE", StringComparison.Ordinal))
        {
            return FakeContainerRuntime.Ok($"SQLBRANCH_WRITABLE={(PoolWritable ? "yes" : "no")}");
        }

        if (script.Contains("SQLBRANCH_FILE", StringComparison.Ordinal))
        {
            return FakeContainerRuntime.Ok(PoolFileExists ? "SQLBRANCH_FILE=yes" : "SQLBRANCH_FILE=no");
        }

        if (script.Contains("stat -c %s", StringComparison.Ordinal))
        {
            return FakeContainerRuntime.Ok("60000000000");
        }

        if (script.Contains("SQLBRANCH_MOUNT", StringComparison.Ordinal))
        {
            return FakeContainerRuntime.Ok(Mounted ? "SQLBRANCH_MOUNT=yes" : "SQLBRANCH_MOUNT=no");
        }

        if (script.Contains("mkfs.xfs", StringComparison.Ordinal))
        {
            PoolFileExists = true;
        }
        else if (script.Contains("mount -o loop", StringComparison.Ordinal))
        {
            if (FailMount)
            {
                return FakeContainerRuntime.Fail(32, "mount: failed to setup loop device");
            }

            Mounted = true;
        }
        else if (script.Contains("umount", StringComparison.Ordinal))
        {
            if (FailUnmount)
            {
                return FakeContainerRuntime.Fail(32, "umount: target is busy");
            }

            Mounted = false;
        }
        else if (script.Contains("rm -f", StringComparison.Ordinal))
        {
            if (FailDelete)
            {
                return FakeContainerRuntime.Fail(1, "rm: cannot remove: Read-only file system");
            }

            PoolFileExists = false;
        }

        return FakeContainerRuntime.Ok();
    }

    /// <summary>Puts an engine of SqlBranch's own in the runtime, as an earlier init would have left it.</summary>
    public void AddExistingEngine(string image, string backend, string pool, string edition = "developer", string port = "14330", bool running = true)
    {
        Runtime.Containers[ContainerLabels.EngineName] = new ContainerInfo(
            ContainerLabels.EngineName,
            image,
            running,
            new Dictionary<string, string>
            {
                [ContainerLabels.Managed] = "true",
                [ContainerLabels.Backend] = backend,
                [ContainerLabels.Pool] = pool,
                [ContainerLabels.Edition] = edition,
                [ContainerLabels.Port] = port,
            },
            running ? [$"127.0.0.1:{port}"] : []);
        EngineDirExists = true;
        MarkerExists = true;
        CredentialsJson = "{\"user\":\"sa\",\"password\":\"Stored#Pass1\"}";
        Connector.SaPassword = "Stored#Pass1";
    }
}
