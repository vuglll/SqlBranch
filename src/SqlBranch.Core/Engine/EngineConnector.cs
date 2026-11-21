using System.Globalization;
using Microsoft.Data.SqlClient;

namespace SqlBranch.Core.Engine;

/// <summary>Where the engine listens on the container host.</summary>
public sealed record EngineEndpoint(string Host, int Port)
{
    /// <summary>The engine's published port on the loopback address.</summary>
    public static EngineEndpoint Loopback(int port) => new("127.0.0.1", port);
}

/// <summary>One open connection to the engine. The only way Core runs SQL.</summary>
public interface IEngineSession : IAsyncDisposable
{
    /// <summary>Runs a query and returns its first column of its first row.</summary>
    Task<object?> ScalarAsync(string sql, CancellationToken cancellationToken);

    /// <summary>Runs a query with named parameters (<c>@name</c>, keys without the at sign) and returns its first column of its first row.</summary>
    Task<object?> ScalarAsync(string sql, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken);

    /// <summary>Runs a statement that returns no rows.</summary>
    Task ExecuteAsync(string sql, CancellationToken cancellationToken);

    /// <summary>Runs a query and returns every row of its first result set.</summary>
    Task<IReadOnlyList<object?[]>> QueryAsync(string sql, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a query with named parameters and returns every row of its first result set as column name
    /// to value (names compare without regard to case). A SQL Server error is thrown as <see cref="EngineSqlException"/>.
    /// </summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryRowsAsync(string sql, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a statement with named parameters and no time limit, for work that takes as long as the data is big,
    /// such as <c>RESTORE DATABASE</c>. A SQL Server error is thrown as <see cref="EngineSqlException"/>.
    /// </summary>
    Task ExecuteAsync(string sql, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken);
}

/// <summary>A SQL Server error, with the numbers of the errors the server reported.</summary>
public sealed class EngineSqlException : Exception
{
    public EngineSqlException()
        : this(string.Empty, [])
    {
    }

    public EngineSqlException(string message)
        : this(message, [])
    {
    }

    public EngineSqlException(string message, Exception innerException)
        : base(message, innerException)
    {
        Numbers = [];
    }

    public EngineSqlException(string message, IReadOnlyList<int> numbers)
        : this(message, numbers, 0)
    {
    }

    public EngineSqlException(string message, IReadOnlyList<int> numbers, int severity)
        : base(message)
    {
        Numbers = numbers;
        Severity = severity;
    }

    /// <summary>The severity class of the error (0 when unknown). Class 20 and above closes the connection.</summary>
    public int Severity { get; }

    private static readonly int[] UnknownOutcomeNumbers = [-2, 0, -1, 64, 233, 10053, 10054];

    /// <summary>
    /// True when the error does not prove the statement was refused: a timeout, a cancel or a broken connection.
    /// A statement that failed this way may still have run on the server.
    /// </summary>
    public bool OutcomeUnknown => Severity >= 20 || Numbers.Any(UnknownOutcomeNumbers.Contains) || Numbers.Count == 0;

    /// <summary>Wraps a SqlClient error, keeping every error number and the severity class.</summary>
    public static EngineSqlException From(SqlException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new EngineSqlException(error.Message, [.. error.Errors.Cast<SqlError>().Select(e => e.Number)], error.Class);
    }

    /// <summary>The error numbers in the order the server sent them.</summary>
    public IReadOnlyList<int> Numbers { get; }

    /// <summary>True when the server reported the error number.</summary>
    public bool Has(int number) => Numbers.Contains(number);
}

/// <summary>Opens sessions as <c>sa</c>. Unit tests use a fake; the real one is <see cref="SqlClientEngineConnector"/>.</summary>
public interface IEngineConnector
{
    /// <summary>Opens a session or throws when the engine does not accept the login.</summary>
    Task<IEngineSession> ConnectAsync(EngineEndpoint endpoint, string password, CancellationToken cancellationToken);

    Task<IEngineSession> ConnectLoginAsync(EngineEndpoint endpoint, string login, string password, string database, CancellationToken cancellationToken);
}

/// <summary>The connector over Microsoft.Data.SqlClient. The password goes into the connection, never onto a command line.</summary>
public sealed class SqlClientEngineConnector : IEngineConnector
{
    public const int ConnectTimeoutSeconds = 5;

    public Task<IEngineSession> ConnectAsync(EngineEndpoint endpoint, string password, CancellationToken cancellationToken) =>
        ConnectLoginAsync(endpoint, "sa", password, "master", cancellationToken);

    public async Task<IEngineSession> ConnectLoginAsync(EngineEndpoint endpoint, string login, string password, string database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = string.Create(CultureInfo.InvariantCulture, $"{endpoint.Host},{endpoint.Port}"),
            UserID = login,
            InitialCatalog = database,
            Password = password,
            TrustServerCertificate = true,
            Pooling = false,
            ConnectTimeout = ConnectTimeoutSeconds,
            ApplicationName = "sqlbranch",
        };

        var connection = new SqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new Session(connection);
    }

    private sealed class Session(SqlConnection connection) : IEngineSession
    {
        public async Task<object?> ScalarAsync(string sql, CancellationToken cancellationToken)
        {
            await using var command = Command(sql);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is DBNull ? null : value;
        }

        public async Task<object?> ScalarAsync(string sql, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            await using var command = Command(sql);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.Add(new SqlParameter(name, value ?? DBNull.Value));
            }

            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is DBNull ? null : result;
        }

        public async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
        {
            try
            {
                await using var command = Command(sql);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqlException ex)
            {
                // Wrapped like the parameterized overload, so a caller can tell a server refusal from a lost connection.
                throw EngineSqlException.From(ex);
            }
        }

        public async Task<IReadOnlyList<object?[]>> QueryAsync(string sql, CancellationToken cancellationToken)
        {
            await using var command = Command(sql);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var rows = new List<object?[]>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                rows.Add(row);
            }

            return rows;
        }

        public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryRowsAsync(
            string sql, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            try
            {
                await using var command = Command(sql, parameters);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var rows = new List<IReadOnlyDictionary<string, object?>>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    }

                    rows.Add(row);
                }

                return rows;
            }
            catch (SqlException ex)
            {
                throw Wrap(ex);
            }
        }

        public async Task ExecuteAsync(string sql, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            try
            {
                await using var command = Command(sql, parameters);
                command.CommandTimeout = 0;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqlException ex)
            {
                throw Wrap(ex);
            }
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();

        private static EngineSqlException Wrap(SqlException ex) => EngineSqlException.From(ex);

        private SqlCommand Command(string sql, IReadOnlyDictionary<string, object?> parameters)
        {
            var command = Command(sql);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.Add(new SqlParameter(name, value ?? DBNull.Value));
            }

            return command;
        }

        private SqlCommand Command(string sql) => new(sql, connection) { CommandTimeout = 120 };
    }
}
