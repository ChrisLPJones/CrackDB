using CrackDB.Web.Jobs;
using CrackDB.Web.Options;
using CrackDB.Web.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CrackDB.Web.Tests;

public class WeeklyCompactionTests
{
    private static DateTimeOffset Utc(string s) =>
        DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.AssumeUniversal);

    [Theory]
    [InlineData("2026-10-13T10:30:00Z", "2026-10-18T00:00:00Z")]   // Tuesday
    [InlineData("2026-10-17T23:59:00Z", "2026-10-18T00:00:00Z")]   // Saturday 23:59
    [InlineData("2026-10-17T23:59:59.9999999Z", "2026-10-18T00:00:00Z")]
    [InlineData("2026-10-18T00:00:00Z", "2026-10-25T00:00:00Z")]   // exactly Sunday 00:00 -> the following Sunday
    [InlineData("2026-10-18T00:00:01Z", "2026-10-25T00:00:00Z")]
    [InlineData("2026-10-18T23:59:59Z", "2026-10-25T00:00:00Z")]
    [InlineData("2026-10-19T00:00:00Z", "2026-10-25T00:00:00Z")]   // Monday
    [InlineData("2026-12-30T12:00:00Z", "2027-01-03T00:00:00Z")]   // across a year end
    [InlineData("2026-10-17T23:00:00-05:00", "2026-10-18T00:00:00Z")]   // non-UTC offset: 04:00Z Sunday -> next Sunday
    public void NextRun_IsNextSundayMidnightUtc(string now, string expected)
    {
        var n = Utc(now);
        var next = WeeklyCompactionService.NextRun(n, TimeZoneInfo.Utc);
        // The offset case: 2026-10-17T23:00-05:00 is 2026-10-18T04:00Z, so the next run is a week later.
        var exp = now.EndsWith("-05:00") ? Utc("2026-10-25T00:00:00Z") : Utc(expected);
        Assert.Equal(exp, next);
        Assert.Equal(TimeSpan.Zero, next.Offset);
        Assert.Equal(DayOfWeek.Sunday, next.DayOfWeek);
        Assert.Equal(TimeSpan.Zero, next.TimeOfDay);
        Assert.True(next > n);
        Assert.True(next - n <= TimeSpan.FromDays(7));
    }

    private static TimeZoneInfo Tz(string id) => TimeZoneInfo.FindSystemTimeZoneById(id);

    [Theory]
    [InlineData("2026-10-13T10:30:00Z")]
    [InlineData("2026-10-18T00:00:00Z")]
    [InlineData("2026-12-30T12:00:00Z")]
    public void NextRun_UtcZone_EqualsUtcOverload(string now) =>
        Assert.Equal(WeeklyCompactionService.NextRun(Utc(now)), WeeklyCompactionService.NextRun(Utc(now), Tz("UTC")));

    [Theory]
    // Sydney is UTC+11 in October (DST): Sunday 00:00 local = Saturday 13:00Z.
    [InlineData("2026-10-18T11:00:00Z", "2026-10-24T13:00:00Z")]   // Sun 22:00 local -> next Sunday
    [InlineData("2026-10-17T12:59:59Z", "2026-10-17T13:00:00Z")]   // 1s before local Sunday midnight
    [InlineData("2026-10-17T13:00:00Z", "2026-10-24T13:00:00Z")]   // exactly local midnight -> following Sunday
    // DST ends Sun 2027-04-04 03:00 local (+11 -> +10): local midnight that day is still +11.
    [InlineData("2027-04-01T00:00:00Z", "2027-04-03T13:00:00Z")]
    // The week after DST ended the offset is +10: Sunday 00:00 local = Saturday 14:00Z.
    [InlineData("2027-04-04T00:00:00Z", "2027-04-10T14:00:00Z")]
    // DST starts Sun 2026-10-04 02:00 local (+10 -> +11): midnight that day is still +10 (Sat 14:00Z).
    [InlineData("2026-10-01T00:00:00Z", "2026-10-03T14:00:00Z")]
    public void NextRun_Sydney_IsLocalSundayMidnight(string now, string expected)
    {
        var n = Utc(now);
        var next = WeeklyCompactionService.NextRun(n, Tz("Australia/Sydney"));
        Assert.Equal(Utc(expected), next);
        Assert.True(next > n);
        Assert.True(next - n <= TimeSpan.FromDays(7));
        var local = TimeZoneInfo.ConvertTime(next, Tz("Australia/Sydney"));
        Assert.Equal(DayOfWeek.Sunday, local.DayOfWeek);
        Assert.Equal(TimeSpan.Zero, local.TimeOfDay);
    }

    [Fact]
    public void NextRun_NonexistentLocalMidnight_MovesToFirstValidTime()
    {
        // America/Sao_Paulo (DST until 2019) skipped 00:00 on the first Sunday of November
        // (2018-11-04 00:00 -> 01:00). The run moves to the first valid time, 01:00 local (-02:00 = 03:00Z).
        var tz = Tz("America/Sao_Paulo");
        var next = WeeklyCompactionService.NextRun(Utc("2018-11-01T00:00:00Z"), tz);
        Assert.Equal(Utc("2018-11-04T03:00:00Z"), next);
    }

    [Fact]
    public void NextRun_AmbiguousLocalMidnight_UsesFirstOccurrence()
    {
        // America/Havana ended DST at 01:00 -> 00:00 on Sunday 2026-11-01, so 00:00 happens twice.
        // The first occurrence is still DST (-04:00 = 04:00Z).
        var tz = Tz("America/Havana");
        var next = WeeklyCompactionService.NextRun(Utc("2026-10-28T00:00:00Z"), tz);
        Assert.Equal(Utc("2026-11-01T04:00:00Z"), next);
    }

    [Theory]
    [InlineData("Australia/Sydney", true)]
    [InlineData("UTC", true)]
    [InlineData("Not/AZone", false)]
    [InlineData("", false)]
    [InlineData("../etc/passwd", false)]
    public void TimeZoneValidator(string id, bool ok)
    {
        var r = new CompactionOptionsValidator().Validate(null, new CompactionOptions { TimeZone = id });
        Assert.Equal(ok, r.Succeeded);
        if (!ok) Assert.Contains("Compaction:TimeZone", r.FailureMessage);
    }

    [Fact]
    public void Default_IsSydney() => Assert.Equal("Australia/Sydney", new CompactionOptions().TimeZone);

    [Fact]
    public async Task InvalidTimeZone_FailsStartup()
    {
        using var f = new CrackDbWebFactory(1000, new() { ["Compaction:TimeZone"] = "Not/AZone" });
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await f.NewLoggedInClientAsync());
        Assert.Contains("Compaction:TimeZone", ex.ToString());
    }

    [Fact]
    public async Task EnqueueAll_QueuesOneCompactJobPerDatabase_AndTheyComplete()
    {
        using var f = new CrackDbWebFactory();
        CrackDbWebFactory.Seed(Path.Combine(f.Root, "Test_b"), ["one", "two"]);
        CrackDbWebFactory.Seed(Path.Combine(f.Root, "Test_c"), ["three"]);
        var c = await f.NewLoggedInClientAsync();      // starts the host

        var dbs = f.Services.GetRequiredService<IDbManager>();
        var queue = f.Services.GetRequiredService<JobQueue>();
        var reg = f.Services.GetRequiredService<JobRegistry>();
        var expected = dbs.ListDatabases().OrderBy(x => x).ToArray();
        Assert.Equal(3, expected.Length);

        var queued = WeeklyCompactionService.EnqueueAll(dbs, queue, NullLogger.Instance);
        Assert.Equal(3, queued);

        var jobs = reg.List().Where(j => j.Kind == "compact").ToList();
        Assert.Equal(expected, jobs.Select(j => j.Db).OrderBy(x => x).ToArray());

        for (int i = 0; i < 2400 && reg.List().Any(j => j.Status is "queued" or "running"); i++)
            await Task.Delay(25);
        Assert.All(reg.List(), j => Assert.Equal("completed", j.Status));
    }
}
