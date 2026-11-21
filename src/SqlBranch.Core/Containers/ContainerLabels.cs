namespace SqlBranch.Core.Containers;

/// <summary>Names SqlBranch gives to the things it creates in the container runtime.</summary>
public static class ContainerLabels
{
    /// <summary>Every container and volume SqlBranch creates carries this label with the value "true".</summary>
    public const string Managed = "io.sqlbranch.managed";

    /// <summary>Engine label: the pool backend, <c>plain</c> or <c>loop-xfs</c>.</summary>
    public const string Backend = "io.sqlbranch.backend";

    /// <summary>Engine label: where the pool is.</summary>
    public const string Pool = "io.sqlbranch.pool";

    /// <summary>Engine label: the edition the engine was created with, as the <c>engineEdition</c> setting names it.</summary>
    public const string Edition = "io.sqlbranch.edition";

    /// <summary>Engine label: the port published on 127.0.0.1.</summary>
    public const string Port = "io.sqlbranch.port";

    /// <summary>The name of the one engine container.</summary>
    public const string EngineName = "sqlbranch-engine";

    /// <summary>The Docker volume used for the copy-mode pool on Docker Desktop.</summary>
    public const string PoolVolume = "sqlbranch-pool";

    /// <summary>Where the pool is mounted inside the engine and work containers.</summary>
    public const string PoolMountPath = "/pool";
}
