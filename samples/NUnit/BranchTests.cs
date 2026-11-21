using Microsoft.Data.SqlClient;
using NUnit.Framework;
using SqlBranch.Testing;

namespace NUnitSample;

[TestFixture]
public sealed class BranchTests
{
    private SqlBranchDatabase? _db;

    [SetUp]
    public async Task SetUp()
    {
        if (Environment.GetEnvironmentVariable("SQLBRANCH_IT") != "1")
        {
            Assert.Ignore("Set SQLBRANCH_IT=1 to run the sample.");
        }
        _db = await SqlBranchDatabase.CreateAsync("shop");
    }

    [Test]
    public async Task ConnectionOpensThePrivateDatabase()
    {
        await using var connection = new SqlConnection(_db!.ConnectionString);
        await connection.OpenAsync();
        Assert.That(connection.Database, Is.EqualTo(_db.Name));
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }
}
