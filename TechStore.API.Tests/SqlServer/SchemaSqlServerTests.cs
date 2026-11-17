using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace TechStore.API.Tests.SqlServer;

[Collection(SqlServerCollection.Name)]
public sealed class SchemaSqlServerTests(SqlServerFixture database)
{
    [SqlServerFact]
    public async Task ModelMatchesMigrationSnapshot_AndDisposableDatabaseHasAllMigrations()
    {
        await using var context = database.Context();
        var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
        var initialized = context.GetService<IModelRuntimeInitializer>().Initialize(snapshot);
        var differences = context.GetService<IMigrationsModelDiffer>().GetDifferences(initialized.GetRelationalModel(), context.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        var description = string.Join("; ", differences.Select(change => change is ColumnOperation column
            ? $"{change.GetType().Name}:{column.Table}.{column.Name}:{column.MaxLength}" : change.GetType().Name));
        Assert.True(differences.Count == 0, description);
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.NotEmpty(await context.Database.GetAppliedMigrationsAsync());
    }
}
