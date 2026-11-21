namespace SqlBranch.Core.Output;

/// <summary>Process exit codes. Scripts and agents rely on these values.</summary>
public enum ExitCode
{
    /// <summary>The command did what it was asked.</summary>
    Success = 0,

    /// <summary>The operation was attempted and failed.</summary>
    OperationFailed = 1,

    /// <summary>The command line or configuration is wrong.</summary>
    UsageError = 2,

    /// <summary>The environment is not ready (Docker, engine, pool).</summary>
    EnvironmentNotReady = 3,
}
