namespace SqlBranch.IntegrationTests;

/// <summary>A test that needs Docker. It runs only when SQLBRANCH_IT=1; otherwise it is reported as skipped.</summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public const string EnableVariable = "SQLBRANCH_IT";

    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnableVariable) != "1")
        {
            Skip = "Needs Docker. Set SQLBRANCH_IT=1 to run it.";
        }
    }
}

/// <summary>A theory that needs Docker. It runs only when SQLBRANCH_IT=1.</summary>
public sealed class IntegrationTheoryAttribute : TheoryAttribute
{
    public IntegrationTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable(IntegrationFactAttribute.EnableVariable) != "1")
        {
            Skip = "Needs Docker. Set SQLBRANCH_IT=1 to run it.";
        }
    }
}

/// <summary>A test that needs Docker and a user-provided SqlPackage executable.</summary>
public sealed class SqlPackageIntegrationFactAttribute : FactAttribute
{
    public SqlPackageIntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(IntegrationFactAttribute.EnableVariable) != "1")
        {
            Skip = "Needs Docker. Set SQLBRANCH_IT=1 to run it.";
        }
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SQLBRANCH_IT_SQLPACKAGE")))
        {
            Skip = "Set SQLBRANCH_IT_SQLPACKAGE to the installed SqlPackage executable.";
        }
    }
}

/// <summary>A scenario that must begin with a format-1 fixture pool.</summary>
public sealed class IntegrationFormat1FactAttribute : FactAttribute
{
    public IntegrationFormat1FactAttribute()
    {
        if (Environment.GetEnvironmentVariable(IntegrationFactAttribute.EnableVariable) != "1")
        {
            Skip = "Needs Docker. Set SQLBRANCH_IT=1 to run it.";
        }
        else if (EngineWorld.RequestedPoolFormat == 2)
        {
            Skip = "Requires a format-1 starting pool to verify the upgrade transition; SQLBRANCH_IT_POOL_FORMAT=2 upgrades the fixture before tests run.";
        }
    }
}
