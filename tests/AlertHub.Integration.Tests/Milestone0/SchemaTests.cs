using AlertHub.Infrastructure.Persistence;
using AlertHub.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertHub.Integration.Tests.Milestone0;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SchemaTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string _cs = string.Empty;

    public async Task InitializeAsync() => _cs = await postgres.CreateDatabaseAsync("schema");
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Migrations_apply_and_leave_nothing_pending()
    {
        await using var db = PostgresFixture.CreateContext(_cs);
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        (await db.Database.GetAppliedMigrationsAsync()).Should().Contain(m => m.EndsWith("_InitialSchema", StringComparison.Ordinal));
    }

    [Fact]
    public async Task All_five_schemas_exist()
    {
        await using var db = PostgresFixture.CreateContext(_cs);
        var schemas = await db.Database.SqlQueryRaw<string>("SELECT nspname AS \"Value\" FROM pg_namespace").ToListAsync();
        schemas.Should().Contain(["alert", "ops", "audit", "hb", "cfg"]);
    }

    [Fact]
    public async Task Raw_event_is_range_partitioned_with_partitions_seven_days_ahead()
    {
        await using var db = PostgresFixture.CreateContext(_cs);
        var strategy = await db.Database.SqlQueryRaw<string>(
            "SELECT partstrat::text AS \"Value\" FROM pg_partitioned_table WHERE partrelid = 'alert.raw_event'::regclass").SingleAsync();
        strategy.Should().Be("r");

        var days = await new RawEventPartitions(db, TimeProvider.System, NullLogger<RawEventPartitions>.Instance).ListAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        days.Should().Contain(today.AddDays(-1)).And.Contain(today).And.Contain(today.AddDays(RawEventPartitions.DefaultDaysAhead));
    }

    [Fact]
    public async Task Partition_creation_is_idempotent()
    {
        await using var db = PostgresFixture.CreateContext(_cs);
        var partitions = new RawEventPartitions(db, TimeProvider.System, NullLogger<RawEventPartitions>.Instance);
        (await partitions.EnsureAsync()).Should().Be(0);
        (await partitions.EnsureAsync(daysAhead: RawEventPartitions.DefaultDaysAhead + 1)).Should().Be(1);
    }

    [Fact]
    public async Task Raw_event_insert_routes_to_the_daily_partition()
    {
        await using var db = PostgresFixture.CreateContext(_cs);
        var now = DateTimeOffset.UtcNow;
        db.RawEvents.Add(new Domain.Alerts.RawEvent
        {
            EventId = Guid.CreateVersion7(now),
            IntegrationId = Guid.NewGuid(),
            ReceivedAt = now,
            Body = "{}"u8.ToArray(),
            SizeBytes = 2,
            ContentType = "application/json",
        });
        await db.SaveChangesAsync();

        var partition = RawEventPartitions.PartitionName(DateOnly.FromDateTime(now.UtcDateTime));
#pragma warning disable EF1002
        var count = await db.Database.SqlQueryRaw<long>($"SELECT count(*) AS \"Value\" FROM alert.\"{partition}\"").SingleAsync();
#pragma warning restore EF1002
        count.Should().Be(1);
    }

    [Fact]
    public async Task Job_table_enforces_one_live_timer_per_episode()
    {
        await using var db = PostgresFixture.CreateContext(_cs);
        var episode = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Domain.Ops.Job Make() => new()
        {
            JobId = Guid.CreateVersion7(),
            Kind = "auto_resolve",
            Payload = "{}",
            EpisodeId = episode,
            NotBefore = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Jobs.Add(Make());
        await db.SaveChangesAsync();
        db.Jobs.Add(Make());
        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Hot_tables_have_fillfactor_and_autovacuum_settings()
    {
        await using var db = PostgresFixture.CreateContext(_cs);
        var options = await db.Database.SqlQueryRaw<string>(
            "SELECT array_to_string(reloptions, ',') AS \"Value\" FROM pg_class WHERE oid = 'ops.job'::regclass").SingleAsync();
        options.Should().Contain("fillfactor=70").And.Contain("autovacuum_vacuum_scale_factor=0.02");
    }
}
