using Microsoft.Data.SqlClient;
using SqlBranch.Testing;
using Xunit;

namespace XunitSample;

public sealed class BranchTests
{
    [SampleFact]
    public async Task ConnectionOpensThePrivateDatabase()
    {
        await using var db = await SqlBranchDatabase.CreateAsync("shop");
        await using var connection = new SqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(db.Name, connection.Database);
    }

    private sealed class SampleFactAttribute : FactAttribute
    {
        public SampleFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SQLBRANCH_IT") != "1")
            {
                Skip = "Set SQLBRANCH_IT=1 with a running SqlBranch engine and a shop image.";
            }
        }
    }
}
