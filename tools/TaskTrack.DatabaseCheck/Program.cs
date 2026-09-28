using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TaskTrack.Repo.Configuration;
using TaskTrack.Repo.Data;

try
{
    var url = Environment.GetEnvironmentVariable("DATABASE_URL");
    if (args.Length == 2 && args[0] == "--secret-file")
    {
        var content = await File.ReadAllTextAsync(args[1]);
        var matches = Regex.Matches(content, "postgres(?:ql)?://[^\\s\"'`]+");
        if (matches.Count != 1) throw new InvalidOperationException();
        url = matches[0].Value;
    }
    if (string.IsNullOrWhiteSpace(url))
    {
        Console.Error.WriteLine("DATABASE_URL is missing. Supply the environment variable or --secret-file PATH.");
        return 1;
    }
    var options = new DbContextOptionsBuilder<TaskManagementDbContext>()
        .UseNpgsql(PostgresConnection.FromUrl(url)).Options;
    await using var db = new TaskManagementDbContext(options);
    await db.Database.OpenConnectionAsync();
    await using var transaction = await db.Database.BeginTransactionAsync();
    await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
    var database = await db.Database.SqlQueryRaw<string>("SELECT current_database() AS \"Value\"").SingleAsync();
    if (database != "tasktrack_ass1") throw new InvalidOperationException();
    var counts = new Dictionary<string, int>
    {
        ["Department"] = await db.Departments.CountAsync(),
        ["Project"] = await db.Projects.CountAsync(),
        ["Task"] = await db.Tasks.CountAsync(),
        ["Tag"] = await db.Tags.CountAsync(),
        ["TaskTag"] = await db.Set<Dictionary<string, object>>("TaskTag").CountAsync()
    };
    if (!counts.Values.SequenceEqual(new[] { 6, 7, 17, 10, 26 }))
        throw new InvalidOperationException();
    var tasks = await db.Tasks.AsNoTracking().Include(t => t.Tags)
        .Include(t => t.Project).ThenInclude(p => p.Department).ToListAsync();
    if (tasks.Sum(t => t.Tags.Count) != 26 || tasks.Any(t => t.Project.Department is null))
        throw new InvalidOperationException();
    await transaction.CommitAsync();
    Console.WriteLine(JsonSerializer.Serialize(new { database, counts, relationshipsVerified = true, readOnly = true }));
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Database verification failed ({exception.GetType().Name}). Connection details withheld.");
    return 1;
}
