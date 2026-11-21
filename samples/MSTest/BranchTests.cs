using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlBranch.Testing;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace MSTestSample;

[TestClass]
public sealed class BranchTests
{
    [TestMethod]
    public async Task ConnectionOpensThePrivateDatabase()
    {
        if (Environment.GetEnvironmentVariable("SQLBRANCH_IT") != "1")
        {
            Assert.Inconclusive("Set SQLBRANCH_IT=1 to run the sample.");
        }
        await using var db = await SqlBranchDatabase.CreateAsync("shop");
        await using var connection = new SqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        Assert.AreEqual(db.Name, connection.Database);
    }
}
