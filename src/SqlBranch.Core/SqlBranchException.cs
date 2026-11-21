using SqlBranch.Core.Output;

namespace SqlBranch.Core;

/// <summary>
/// A failure with a stable error code. Commands turn it into a result with
/// that code and its exit code.
/// </summary>
public sealed class SqlBranchException : Exception
{
    public SqlBranchException()
        : this(ErrorCodes.OperationFailed, string.Empty)
    {
    }

    public SqlBranchException(string message)
        : this(ErrorCodes.OperationFailed, message)
    {
    }

    public SqlBranchException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = ErrorCodes.OperationFailed;
    }

    public SqlBranchException(ErrorCode code, string message)
        : this(code, message, null)
    {
    }

    internal SqlBranchException(ErrorCode code, string message, Exception? innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>The error code to report.</summary>
    public ErrorCode Code { get; }
}
