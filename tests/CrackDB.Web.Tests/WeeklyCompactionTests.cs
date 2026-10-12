using CrackDB.Web.Jobs;
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
        var next = WeeklyCompactionService.NextRun(n);
        // The offset case: 2026-10-17T23:00-05:00 is 2026-10-18T04:00Z, so the next run is a week later.
        var exp = now.EndsWith("-05:00") ? Utc("2026-10-25T00:00:00Z") : Utc(expected);
        Assert.Equal(exp, next);
        Assert.Equal(TimeSpan.Zero, next.Offset);
        Assert.Equal(DayOfWeek.Sunday, next.DayOfWeek);
        Assert.Equal(TimeSpan.Zero, next.TimeOfDay);
        Assert.True(next > n);
        Assert.True(next - n <= TimeSpan.FromDays(7));
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
