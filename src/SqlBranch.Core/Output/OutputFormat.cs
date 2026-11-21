namespace SqlBranch.Core.Output;

/// <summary>How a command prints its result.</summary>
public enum OutputFormat
{
    /// <summary>Human-readable text.</summary>
    Text,

    /// <summary>One JSON document on standard output.</summary>
    Json,
}
