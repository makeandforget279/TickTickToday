using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using Cronos;

const string ApiBaseUrl = "https://api.ticktick.com/open/v1";
const string AuthorizeUrl = "https://ticktick.com/oauth/authorize";
const string TokenUrl = "https://ticktick.com/oauth/token";
const string InboxProjectId = "inbox";
const string DefaultRedirectUri = "http://127.0.0.1:8089/";
const string DefaultEnvironmentFileName = ".env";
const string BucharestTimeZoneId = "Europe/Bucharest";
const string WindowsBucharestTimeZoneId = "E. Europe Standard Time";
const string Scope = "tasks:read tasks:write";
const string PeriodicTaskCleanupTag = "closewhenold";
const int DefaultLookbackDays = 60;
const int DefaultPlanDays = 60;
const int DefaultPeriodicDays = 0;
const int MaxDisplayedTaskTitleLength = 100;
var bucharestTimeZone = GetBucharestTimeZone();
AppSettings appSettings;
try
{
    if (args.Length != 1)
    {
        throw new InvalidOperationException("Usage: TickTickToday <path-to-ini>");
    }

    appSettings = AppSettings.Load(args[0], DefaultLookbackDays, DefaultPlanDays, DefaultPeriodicDays);
}
catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or IOException)
{
    Console.Error.WriteLine($"Settings error: {ex.Message}");
    return 1;
}

var runOptions = appSettings.Options;

List<PeriodicTaskRule> periodicTaskRules;
try
{
    periodicTaskRules = LoadPeriodicTaskRules(appSettings.PeriodicTasksFile);
}
catch (Exception ex) when (ex is InvalidOperationException or IOException or XmlException)
{
    Console.Error.WriteLine($"Periodic task error: {ex.Message}");
    return 1;
}

if (runOptions.CheckConfig)
{
    Console.WriteLine("Configuration is valid.");
    Console.WriteLine($"Periodic rules loaded: {periodicTaskRules.Count}");
    if (runOptions.PeriodicDays > 0)
    {
        var checkDay = TimeZoneInfo.ConvertTime(DateTimeOffset.Now, bucharestTimeZone).Date;
        var checkedOccurrences = BuildPeriodicTaskOccurrences(
            periodicTaskRules,
            bucharestTimeZone,
            checkDay,
            runOptions.PeriodicDays);
        Console.WriteLine($"Cron occurrences in the next {runOptions.PeriodicDays} days: {checkedOccurrences.Count}");
    }
    else
    {
        Console.WriteLine("Periodic task creation is disabled (periodic-days=0).");
    }

    return 0;
}

var environmentValues = EnvironmentFile.LoadOptional(DefaultEnvironmentFileName);
var clientId = GetSetting("TICKTICK_CLIENT_ID", environmentValues);
var clientSecret = GetSetting("TICKTICK_CLIENT_SECRET", environmentValues);
var accessToken = GetSetting("TICKTICK_ACCESS_TOKEN", environmentValues);
var redirectUri = GetSetting("TICKTICK_REDIRECT_URI", environmentValues, appSettings.RedirectUri) ?? DefaultRedirectUri;

if (string.IsNullOrWhiteSpace(accessToken) &&
    (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret)))
{
    Console.Error.WriteLine("Set TICKTICK_ACCESS_TOKEN or TICKTICK_CLIENT_ID + TICKTICK_CLIENT_SECRET before running.");
    Console.Error.WriteLine("Copy .env.example to .env and add your credentials, or use PowerShell:");
    Console.Error.WriteLine("$env:TICKTICK_ACCESS_TOKEN='your-token'");
    Console.Error.WriteLine();
    Console.Error.WriteLine("OAuth:");
    Console.Error.WriteLine("$env:TICKTICK_CLIENT_ID='your-client-id'");
    Console.Error.WriteLine("$env:TICKTICK_CLIENT_SECRET='your-client-secret'");
    Console.Error.WriteLine("$env:TICKTICK_REDIRECT_URI='http://127.0.0.1:8089/'");
    return 1;
}

var options = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    WriteIndented = true
};

var runStartedAt = DateTimeOffset.Now;

using var http = new HttpClient();
var tokenStore = new TokenStore(Path.Combine(Directory.GetCurrentDirectory(), ".ticktick-token.json"), options);

if (string.IsNullOrWhiteSpace(accessToken))
{
    var token = runOptions.ForceLogin ? null : await tokenStore.LoadAsync();

    if (token is null || token.IsExpired)
    {
        token = await AuthorizeAsync(http, tokenStore, clientId!, clientSecret!, redirectUri, options);
    }

    accessToken = token.AccessToken;
}

try
{
    var today = runStartedAt.Date;
    var firstDay = today.AddDays(-runOptions.LookbackDays);
    var allDueTasks = new List<TaskWithProject>();
    var allTasks = await GetAllTasksAsync(accessToken, options);
    List<PeriodicTaskCreateResult> createdPeriodicTasks = [];
    if (runOptions.PeriodicDays > 0)
    {
        var plannedPeriodicTasks = BuildPeriodicTaskOccurrences(
            periodicTaskRules,
            bucharestTimeZone,
            today,
            runOptions.PeriodicDays);

        Console.WriteLine("Periodic tasks to process:");
        if (plannedPeriodicTasks.Count == 0)
        {
            Console.WriteLine("  No periodic tasks occur in the configured range.");
        }
        else
        {
            foreach (var task in plannedPeriodicTasks)
            {
                Console.WriteLine($"  - {task.Title} [{task.ListName}, ID: {task.Identifier}] - {task.Start:yyyy-MM-dd}, {task.Start:HH:mm}-{task.End:HH:mm}");
            }
        }

        createdPeriodicTasks = await CreatePeriodicTasksAsync(
            plannedPeriodicTasks,
            allTasks,
            accessToken,
            options,
            runOptions.Simulate);
    }

    var closedOldTaggedTasks = runOptions.CloseWhenOld
        ? await CloseOldTaggedTasksAsync(allTasks, accessToken, runStartedAt, runOptions.Simulate)
        : [];
    var movedTasks = new List<TaskMoveResult>();

    Console.WriteLine($"Incomplete TickTick tasks due between {firstDay:yyyy-MM-dd} and {today:yyyy-MM-dd}:");
    if (runOptions.PeriodicDays > 0)
    {
        Console.WriteLine($"Periodic tasks: populated for the next {runOptions.PeriodicDays} days.");
    }
    if (runOptions.MoveLimit is not null)
    {
        Console.WriteLine($"Move mode: at most {runOptions.MoveLimit.Value} tasks per run (move={runOptions.MoveLimit.Value}).");
        Console.WriteLine($"Planning: searching for free slots between {runOptions.DayStart:HH:mm} and {runOptions.DayEnd:HH:mm} over the next {runOptions.PlanDays} days.");
    }
    if (runOptions.Simulate)
    {
        Console.WriteLine("Simulation mode: TickTick will not be updated (simulate=1).");
    }
    if (closedOldTaggedTasks.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Overdue #closewhenold tasks:");
        foreach (var result in closedOldTaggedTasks)
        {
            WriteColoredLine(
                result.Success
                    ? $"{(runOptions.Simulate ? "  Simulated completion" : "  Completed")}: {TruncateTaskTitle(result.Task.Task.Title)} ({result.Task.ProjectName})"
                    : $"  Could not complete: {TruncateTaskTitle(result.Task.Task.Title)} ({result.Task.ProjectName}) - {result.Message}",
                result.Success ? ConsoleColor.Yellow : ConsoleColor.Red);
        }
    }
    if (createdPeriodicTasks.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Periodic tasks:");
        foreach (var result in createdPeriodicTasks)
        {
            WriteColoredLine(
                result.Success
                    ? $"{(result.Created ? runOptions.Simulate ? "  Simulated creation" : "  Created" : "  Already exists")}: {result.Title} ({result.Start:yyyy-MM-dd HH:mm} -> {result.End:HH:mm})"
                    : $"  Could not create: {result.Title} ({result.Start:yyyy-MM-dd HH:mm}) - {result.Message}",
                result.Success ? ConsoleColor.Yellow : ConsoleColor.Red);
        }
    }

    foreach (var day in EnumerateLookupDays(firstDay, today))
    {
        var dueTasks = GetDayTasks(day, allTasks, runStartedAt);

        allDueTasks.AddRange(dueTasks);

        Console.WriteLine();
        Console.WriteLine($"{day:yyyy-MM-dd}:");

        if (dueTasks.Count == 0)
        {
            Console.WriteLine("  No incomplete due tasks found.");
            continue;
        }

        foreach (var item in dueTasks)
        {
            PrintTask(item);

            if (IsOverdueMoveCandidate(day, today))
            {
                if (HasDoNotMoveTag(item.Task))
                {
                    WriteColoredLine("    Move skipped: the task has the #donotmove tag.", ConsoleColor.Yellow);
                    continue;
                }

                if (runOptions.MoveLimit is { } moveLimit && movedTasks.Count(result => result.Success) >= moveLimit)
                {
                    Console.WriteLine($"    Move skipped: the move={moveLimit} limit was reached.");
                    continue;
                }

                var moveResult = await MoveTaskAsync(
                    today,
                    item,
                    allTasks,
                    periodicTaskRules,
                    accessToken,
                    options,
                    bucharestTimeZone,
                    runStartedAt,
                    runOptions.Simulate,
                    runOptions.PlanDays,
                    runOptions.DayStart,
                    runOptions.DayEnd);
                movedTasks.Add(moveResult);
                WriteColoredLine(
                    moveResult.Success
                        ? $"{(runOptions.Simulate ? "    Simulated move" : "    Moved")} to {moveResult.NewStart:yyyy-MM-dd HH:mm} -> {moveResult.NewDue:HH:mm}"
                        : $"    Could not move: {moveResult.Message}",
                    ConsoleColor.Red);
            }
        }
    }

    if (allDueTasks.Count == 0)
    {
        Console.WriteLine();
        Console.WriteLine("No incomplete due tasks were found in the configured range.");
        return 0;
    }

    if (movedTasks.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"Tasks moved: {movedTasks.Count(item => item.Success)} / {movedTasks.Count}");
    }
    if (closedOldTaggedTasks.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"#closewhenold tasks completed: {closedOldTaggedTasks.Count(item => item.Success)} / {closedOldTaggedTasks.Count}");
    }
    if (createdPeriodicTasks.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"Periodic tasks created: {createdPeriodicTasks.Count(item => item.Success && item.Created)} / {createdPeriodicTasks.Count}");
    }

    return 0;
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"HTTP error: {ex.Message}");
    Console.Error.WriteLine("If the token expired or was revoked, delete .ticktick-token.json and run the application again.");
    return 1;
}

static List<PeriodicTaskRule> LoadPeriodicTaskRules(string fileName)
{
    return PeriodicTaskSchedule.Load(fileName);
}

static string? GetSetting(
    string environmentVariableName,
    IReadOnlyDictionary<string, string> environmentValues,
    string? configuredValue = null)
{
    var environmentValue = Environment.GetEnvironmentVariable(environmentVariableName);
    if (!string.IsNullOrWhiteSpace(environmentValue))
    {
        return environmentValue.Trim();
    }

    if (environmentValues.TryGetValue(environmentVariableName, out var fileValue) &&
        !string.IsNullOrWhiteSpace(fileValue))
    {
        return fileValue.Trim();
    }

    return string.IsNullOrWhiteSpace(configuredValue) ? null : configuredValue.Trim();
}

static IEnumerable<DateTime> EnumerateLookupDays(DateTime firstDay, DateTime today)
{
    for (var day = today.AddDays(-1); day >= firstDay; day = day.AddDays(-1))
    {
        yield return day;
    }

    if (firstDay <= today)
    {
        yield return today;
    }
}

static List<TaskWithProject> GetDayTasks(DateTimeOffset date, IEnumerable<TaskWithProject> tasks, DateTimeOffset now)
{
    var localDate = date.LocalDateTime.Date;
    var today = now.LocalDateTime.Date;

    return tasks
        .Where(item => item.Task.Status != 2)
        .Where(item => TryGetTaskDueDate(item.Task, out var taskDate) && taskDate.LocalDateTime.Date == localDate)
        .Where(item => localDate != today || (TryGetTaskDueDate(item.Task, out var taskDate) && taskDate < now))
        .OrderBy(item => TryGetTaskDueDate(item.Task, out var taskDate) ? taskDate : DateTimeOffset.MaxValue)
        .ThenBy(item => item.Task.Title, StringComparer.CurrentCultureIgnoreCase)
        .ToList();
}

static List<PeriodicTaskOccurrence> BuildPeriodicTaskOccurrences(
    IReadOnlyCollection<PeriodicTaskRule> periodicTaskRules,
    TimeZoneInfo bucharestTimeZone,
    DateTime today,
    int periodicDays)
{
    var occurrences = new List<PeriodicTaskOccurrence>();

    for (var dayOffset = 0; dayOffset < periodicDays; dayOffset++)
    {
        var day = today.AddDays(dayOffset);
        foreach (var rule in periodicTaskRules)
        {
            foreach (var start in rule.Schedule.GetOccurrences(day, bucharestTimeZone))
            {
                occurrences.Add(new PeriodicTaskOccurrence(
                    rule.Title,
                    rule.ListName,
                    rule.Identifier,
                    start,
                    start.Add(rule.Duration)));
            }
        }
    }

    return occurrences
        .OrderBy(occurrence => occurrence.Start)
        .ThenBy(occurrence => occurrence.Title, StringComparer.CurrentCultureIgnoreCase)
        .ToList();
}

static async Task<List<PeriodicTaskCreateResult>> CreatePeriodicTasksAsync(
    IReadOnlyCollection<PeriodicTaskOccurrence> plannedPeriodicTasks,
    List<TaskWithProject> allTasks,
    string accessToken,
    JsonSerializerOptions options,
    bool simulate)
{
    var results = new List<PeriodicTaskCreateResult>();

    if (plannedPeriodicTasks.Count == 0)
    {
        return results;
    }

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    var projects = await GetProjectsAsync(http, options);

    foreach (var task in plannedPeriodicTasks)
    {
        try
        {
            var project = ResolvePeriodicTaskProject(task.ListName, projects);

            if (PeriodicTaskExists(allTasks, task, project.Name))
            {
                results.Add(PeriodicTaskCreateResult.ExistingResult(task.Title, task.Start, task.End));
                continue;
            }

            TickTickTask createdTask;
            if (simulate)
            {
                createdTask = CreateLocalPeriodicTask(task, project.Id);
            }
            else
            {
                createdTask = await CreateTaskAsync(http, task, project.Id, options);
            }

            allTasks.Add(new TaskWithProject(createdTask, project.Name));
            results.Add(PeriodicTaskCreateResult.CreatedResult(task.Title, task.Start, task.End));
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            results.Add(PeriodicTaskCreateResult.Failure(task.Title, task.Start, task.End, ex.Message));
        }
    }

    return results;
}

static (string Id, string Name) ResolvePeriodicTaskProject(string listName, IReadOnlyCollection<Project> projects)
{
    if (string.Equals(listName.Trim(), "Inbox", StringComparison.OrdinalIgnoreCase))
    {
        return (InboxProjectId, "Inbox");
    }

    var matches = projects
        .Where(project => !string.IsNullOrWhiteSpace(project.Id))
        .Where(project => string.Equals(project.Name?.Trim(), listName.Trim(), StringComparison.CurrentCultureIgnoreCase))
        .ToList();

    if (matches.Count == 0)
    {
        var normalizedListName = NormalizeTickTickListName(listName);
        matches = projects
            .Where(project => !string.IsNullOrWhiteSpace(project.Id))
            .Where(project => string.Equals(
                NormalizeTickTickListName(project.Name),
                normalizedListName,
                StringComparison.CurrentCultureIgnoreCase))
            .ToList();
    }

    return matches.Count switch
    {
        1 => (matches[0].Id!, matches[0].Name?.Trim() ?? listName.Trim()),
        0 => throw new InvalidOperationException($"The TickTick list '{listName}' does not exist."),
        _ => throw new InvalidOperationException($"Multiple TickTick lists are named '{listName}'; the name must be unique.")
    };
}

static string NormalizeTickTickListName(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return "";
    }

    return new string(value
        .Trim()
        .Where(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character))
        .ToArray())
        .Trim();
}

static string CreatePeriodicTaskDescription(string identifier) =>
    $"Generated automatically by TickTickToday. Periodic rule ID: {identifier}";

static bool HasPeriodicTaskIdentifier(TickTickTask task, string identifier)
{
    const string Marker = "Periodic rule ID:";
    if (string.IsNullOrWhiteSpace(task.Content))
    {
        return false;
    }

    var markerIndex = task.Content.LastIndexOf(Marker, StringComparison.OrdinalIgnoreCase);
    if (markerIndex >= 0)
    {
        var storedIdentifier = task.Content[(markerIndex + Marker.Length)..].Trim();
        return string.Equals(storedIdentifier, identifier, StringComparison.OrdinalIgnoreCase);
    }

    // Recognize descriptions generated by older releases without retaining
    // language-specific marker text. This prevents duplicate scheduled tasks.
    return task.Content.Contains("TickTickToday", StringComparison.OrdinalIgnoreCase) &&
        task.Content.TrimEnd().EndsWith(identifier, StringComparison.OrdinalIgnoreCase);
}

static bool PeriodicTaskExists(
    IEnumerable<TaskWithProject> allTasks,
    PeriodicTaskOccurrence plannedTask,
    string projectName)
{
    return allTasks
        .Where(item => item.Task.Status != 2)
        .Where(item => string.Equals(item.ProjectName, projectName, StringComparison.CurrentCultureIgnoreCase))
        .Any(item =>
            HasPeriodicTaskIdentifier(item.Task, plannedTask.Identifier) &&
            TryGetTaskInterval(item.Task, out var interval) &&
            interval is not null &&
            interval.Start == plannedTask.Start &&
            interval.End == plannedTask.End);
}

static TickTickTask CreateLocalPeriodicTask(
    PeriodicTaskOccurrence task,
    string projectId)
{
    return new TickTickTask
    {
        Id = $"simulate-periodic-{Guid.NewGuid():N}",
        ProjectId = projectId,
        Title = task.Title,
        Content = CreatePeriodicTaskDescription(task.Identifier),
        StartDate = FormatTickTickDate(task.Start),
        DueDate = FormatTickTickDate(task.End),
        TimeZone = BucharestTimeZoneId,
        IsAllDay = false,
        Reminders = ["TRIGGER:PT0S"],
        Status = 0,
        Priority = 0,
        Tags = [PeriodicTaskCleanupTag]
    };
}

static bool IsOverdueMoveCandidate(DateTimeOffset day, DateTime today)
{
    return day.LocalDateTime.Date <= today;
}

static async Task<TaskMoveResult> MoveTaskAsync(
    DateTime day,
    TaskWithProject task,
    List<TaskWithProject> allTasks,
    IReadOnlyCollection<PeriodicTaskRule> periodicTaskRules,
    string accessToken,
    JsonSerializerOptions options,
    TimeZoneInfo bucharestTimeZone,
    DateTimeOffset now,
    bool simulate,
    int planDays,
    TimeOnly planningDayStart,
    TimeOnly planningDayEnd)
{
    var duration = GetTaskDuration(task.Task);

    if (duration <= TimeSpan.Zero)
    {
        duration = TimeSpan.FromMinutes(30);
    }

    var planningWindow = planningDayEnd.ToTimeSpan() - planningDayStart.ToTimeSpan();
    if (duration > planningWindow)
    {
        return TaskMoveResult.Failure(
            task,
            $"The task duration is too long for the {planningDayStart:HH:mm}-{planningDayEnd:HH:mm} planning window ({duration}).");
    }

    var candidateDay = day.Date;

    for (var searchedDays = 0; searchedDays < planDays; searchedDays++)
    {
        var candidate = FindFreeSlot(
            candidateDay,
            task.Task,
            allTasks,
            periodicTaskRules,
            duration,
            bucharestTimeZone,
            now,
            planningDayStart,
            planningDayEnd);

        if (candidate is not null)
        {
            var newStart = candidate.Value;
            var newDue = newStart.Add(duration);

            if (!simulate)
            {
                await UpdateTaskDatesAsync(task.Task, newStart, newDue, accessToken, options);
            }

            task.Task.StartDate = FormatTickTickDate(newStart);
            task.Task.DueDate = FormatTickTickDate(newDue);
            task.Task.Reminders = ["TRIGGER:PT0S"];
            task.Task.IsAllDay = false;
            task.Task.TimeZone = BucharestTimeZoneId;

            return TaskMoveResult.SuccessResult(task, newStart, newDue);
        }

        candidateDay = candidateDay.AddDays(1);
    }

    return TaskMoveResult.Failure(task, $"No free slot was found in the next {planDays} days.");
}

static DateTimeOffset? FindFreeSlot(
    DateTime day,
    TickTickTask task,
    IEnumerable<TaskWithProject> allTasks,
    IReadOnlyCollection<PeriodicTaskRule> periodicTaskRules,
    TimeSpan duration,
    TimeZoneInfo bucharestTimeZone,
    DateTimeOffset now,
    TimeOnly planningDayStart,
    TimeOnly planningDayEnd)
{
    var dayStart = CreateBucharestDateTime(day, planningDayStart, bucharestTimeZone);
    var dayEnd = CreateBucharestDateTime(day, planningDayEnd, bucharestTimeZone);
    var localNow = TimeZoneInfo.ConvertTime(now, bucharestTimeZone);

    if (day.Date == localNow.Date)
    {
        dayStart = MaxDateTimeOffset(dayStart, RoundUpToNextHalfHour(localNow));
    }

    var candidates = new List<DateTimeOffset>();

    if (TryGetOriginalStartTime(task, out var originalStartTime))
    {
        var sameTime = CreateBucharestDateTime(day, originalStartTime, bucharestTimeZone);
        if (sameTime >= dayStart && sameTime.Add(duration) <= dayEnd)
        {
            candidates.Add(sameTime);
        }
    }

    for (var candidate = dayStart; candidate.Add(duration) <= dayEnd; candidate = candidate.AddMinutes(30))
    {
        if (!candidates.Contains(candidate))
        {
            candidates.Add(candidate);
        }
    }

    var busyIntervals = GetBusyIntervals(day, task.Id, allTasks, periodicTaskRules, bucharestTimeZone);

    foreach (var candidate in candidates)
    {
        if (!busyIntervals.Any(interval => IntervalsOverlap(candidate, candidate.Add(duration), interval.Start, interval.End)))
        {
            return candidate;
        }
    }

    return null;
}

static DateTimeOffset RoundUpToNextHalfHour(DateTimeOffset value)
{
    var rounded = new DateTimeOffset(
        value.Year,
        value.Month,
        value.Day,
        value.Hour,
        value.Minute,
        0,
        value.Offset);

    var minutesToAdd = rounded.Minute switch
    {
        0 => 0,
        <= 30 => 30 - rounded.Minute,
        _ => 60 - rounded.Minute
    };

    return rounded.AddMinutes(minutesToAdd);
}

static DateTimeOffset MaxDateTimeOffset(DateTimeOffset first, DateTimeOffset second)
{
    return first >= second ? first : second;
}

static List<TaskInterval> GetBusyIntervals(
    DateTime day,
    string? ignoredTaskId,
    IEnumerable<TaskWithProject> allTasks,
    IReadOnlyCollection<PeriodicTaskRule> periodicTaskRules,
    TimeZoneInfo bucharestTimeZone)
{
    var taskIntervals = allTasks
        .Where(item => item.Task.Status != 2)
        .Where(item => item.Task.Id != ignoredTaskId)
        .SelectMany(item => GetTaskIntervalsForDay(day, item.Task, bucharestTimeZone));

    // Include the previous day for rules that cross midnight.
    // The maximum duration accepted in XML is 24 hours.
    var periodicIntervals = new[] { day.Date.AddDays(-1), day.Date }
        .SelectMany(scheduleDay => periodicTaskRules.SelectMany(rule =>
            rule.Schedule.GetOccurrences(scheduleDay, bucharestTimeZone)
                .Select(start => new TaskInterval(start, start.Add(rule.Duration)))));

    return taskIntervals
        .Concat(periodicIntervals)
        .OrderBy(interval => interval.Start)
        .ToList();
}

static IEnumerable<TaskInterval> GetTaskIntervalsForDay(DateTime day, TickTickTask task, TimeZoneInfo bucharestTimeZone)
{
    if (!TryGetTaskInterval(task, out var interval) || interval is null)
    {
        yield break;
    }

    var originalStartDate = interval.Start.LocalDateTime.Date;
    var originalEndDate = interval.End.LocalDateTime.Date;
    if (originalStartDate == day.Date || originalEndDate == day.Date)
    {
        yield return interval;
        yield break;
    }

    if (!TryGetRecurringIntervalForDay(day, task, interval, bucharestTimeZone, out var recurringInterval) ||
        recurringInterval is null)
    {
        yield break;
    }

    yield return recurringInterval;
}

static bool TryGetRecurringIntervalForDay(
    DateTime day,
    TickTickTask task,
    TaskInterval originalInterval,
    TimeZoneInfo bucharestTimeZone,
    out TaskInterval? interval)
{
    interval = null;

    if (string.IsNullOrWhiteSpace(task.RepeatFlag))
    {
        return false;
    }

    var rule = ParseRepeatRule(task.RepeatFlag);
    if (!rule.TryGetValue("FREQ", out var frequency))
    {
        return false;
    }

    var originalDay = originalInterval.Start.LocalDateTime.Date;
    if (day.Date <= originalDay)
    {
        return false;
    }

    if (IsSkippedWeekend(day, rule))
    {
        return false;
    }

    var repeatInterval = GetRepeatInterval(rule);
    var occursOnDay = frequency.ToUpperInvariant() switch
    {
        "DAILY" => OccursDaily(day, originalDay, repeatInterval),
        "WEEKLY" => OccursWeekly(day, originalDay, repeatInterval, rule),
        "MONTHLY" => OccursMonthly(day, originalDay, repeatInterval, rule),
        _ => false
    };

    if (!occursOnDay)
    {
        return false;
    }

    var startTime = TimeOnly.FromDateTime(originalInterval.Start.LocalDateTime);
    var start = CreateBucharestDateTime(day.Date, startTime, bucharestTimeZone);
    interval = new TaskInterval(start, start.Add(originalInterval.End - originalInterval.Start));
    return true;
}

static bool OccursDaily(DateTime day, DateTime originalDay, int repeatInterval)
{
    var daysAfterOriginal = (day.Date - originalDay.Date).Days;
    return daysAfterOriginal > 0 && daysAfterOriginal % repeatInterval == 0;
}

static bool OccursWeekly(DateTime day, DateTime originalDay, int repeatInterval, Dictionary<string, string> rule)
{
    var daysAfterOriginal = (day.Date - originalDay.Date).Days;
    if (daysAfterOriginal <= 0 || daysAfterOriginal / 7 % repeatInterval != 0)
    {
        return false;
    }

    if (!rule.TryGetValue("BYDAY", out var byDay))
    {
        return day.DayOfWeek == originalDay.DayOfWeek;
    }

    return byDay
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(ParseRRuleDayOfWeek)
        .Any(dayOfWeek => dayOfWeek == day.DayOfWeek);
}

static bool OccursMonthly(DateTime day, DateTime originalDay, int repeatInterval, Dictionary<string, string> rule)
{
    var monthsAfterOriginal = (day.Year - originalDay.Year) * 12 + day.Month - originalDay.Month;
    if (monthsAfterOriginal <= 0 || monthsAfterOriginal % repeatInterval != 0)
    {
        return false;
    }

    if (!rule.TryGetValue("BYMONTHDAY", out var byMonthDay))
    {
        return day.Day == originalDay.Day;
    }

    return byMonthDay
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var monthDay) &&
            monthDay == day.Day);
}

static int GetRepeatInterval(Dictionary<string, string> rule)
{
    return rule.TryGetValue("INTERVAL", out var intervalText) &&
        int.TryParse(intervalText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedInterval) &&
        parsedInterval > 0
            ? parsedInterval
            : 1;
}

static bool IsSkippedWeekend(DateTime day, Dictionary<string, string> rule)
{
    return rule.TryGetValue("TT_SKIP", out var skip) &&
        string.Equals(skip, "WEEKEND", StringComparison.OrdinalIgnoreCase) &&
        (day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday);
}

static DayOfWeek? ParseRRuleDayOfWeek(string value)
{
    return value.ToUpperInvariant() switch
    {
        "MO" => DayOfWeek.Monday,
        "TU" => DayOfWeek.Tuesday,
        "WE" => DayOfWeek.Wednesday,
        "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday,
        "SA" => DayOfWeek.Saturday,
        "SU" => DayOfWeek.Sunday,
        _ => null
    };
}

static Dictionary<string, string> ParseRepeatRule(string repeatFlag)
{
    const string Prefix = "RRULE:";
    var ruleText = repeatFlag.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
        ? repeatFlag[Prefix.Length..]
        : repeatFlag;

    return ruleText
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
        .Where(parts => parts.Length == 2)
        .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
}

static bool TryGetTaskInterval(TickTickTask task, out TaskInterval? interval)
{
    interval = null;

    if (!TryParseTickTickDate(task.DueDate, out var dueDate))
    {
        return false;
    }

    var startDate = TryParseTickTickDate(task.StartDate, out var parsedStartDate)
        ? parsedStartDate
        : dueDate.AddMinutes(-30);

    if (dueDate <= startDate)
    {
        dueDate = startDate.AddMinutes(30);
    }

    interval = new TaskInterval(startDate, dueDate);
    return true;
}

static TimeSpan GetTaskDuration(TickTickTask task)
{
    if (TryParseTickTickDate(task.StartDate, out var startDate) &&
        TryParseTickTickDate(task.DueDate, out var dueDate) &&
        dueDate > startDate)
    {
        return dueDate - startDate;
    }

    return TimeSpan.FromMinutes(30);
}

static bool TryGetOriginalStartTime(TickTickTask task, out TimeOnly time)
{
    if (TryParseTickTickDate(task.StartDate, out var startDate))
    {
        time = TimeOnly.FromDateTime(startDate.LocalDateTime);
        return true;
    }

    if (TryParseTickTickDate(task.DueDate, out var dueDate))
    {
        time = TimeOnly.FromDateTime(dueDate.LocalDateTime);
        return true;
    }

    time = default;
    return false;
}

static bool IntervalsOverlap(DateTimeOffset start, DateTimeOffset end, DateTimeOffset otherStart, DateTimeOffset otherEnd)
{
    return start < otherEnd && end > otherStart;
}

static DateTimeOffset CreateBucharestDateTime(DateTime day, TimeOnly time, TimeZoneInfo bucharestTimeZone)
{
    var dateTime = new DateTime(day.Year, day.Month, day.Day, time.Hour, time.Minute, 0, DateTimeKind.Unspecified);
    return new DateTimeOffset(dateTime, bucharestTimeZone.GetUtcOffset(dateTime));
}

static TimeZoneInfo GetBucharestTimeZone()
{
    try
    {
        return TimeZoneInfo.FindSystemTimeZoneById(BucharestTimeZoneId);
    }
    catch (TimeZoneNotFoundException)
    {
        return TimeZoneInfo.FindSystemTimeZoneById(WindowsBucharestTimeZoneId);
    }
}

static async Task UpdateTaskDatesAsync(
    TickTickTask task,
    DateTimeOffset newStart,
    DateTimeOffset newDue,
    string accessToken,
    JsonSerializerOptions options)
{
    if (string.IsNullOrWhiteSpace(task.Id) || string.IsNullOrWhiteSpace(task.ProjectId))
    {
        throw new InvalidOperationException("The task has no id or projectId and cannot be moved.");
    }

    if (newStart == default || newDue == default || newDue <= newStart)
    {
        throw new InvalidOperationException($"Invalid interval for moving task '{task.Title}': {newStart:o} -> {newDue:o}");
    }

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    var payload = new UpdateTaskRequest
    {
        Id = task.Id,
        ProjectId = task.ProjectId,
        Title = task.Title ?? "",
        Content = task.Content ?? "",
        StartDate = FormatTickTickDate(newStart),
        DueDate = FormatTickTickDate(newDue),
        TimeZone = BucharestTimeZoneId,
        IsAllDay = false,
        Reminders = ["TRIGGER:PT0S"],
        Priority = task.Priority,
        Tags = task.Tags ?? []
    };

    using var content = JsonContent.Create(payload, options: options);
    using var response = await http.PostAsync($"{ApiBaseUrl}/task/{Uri.EscapeDataString(task.Id)}", content);
    var responseBody = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        throw new HttpRequestException($"Task update failed: {(int)response.StatusCode} {response.ReasonPhrase}. {responseBody}");
    }
}

static async Task<TickTickTask> CreateTaskAsync(
    HttpClient http,
    PeriodicTaskOccurrence task,
    string projectId,
    JsonSerializerOptions options)
{
    if (task.End <= task.Start)
    {
        throw new InvalidOperationException($"Invalid interval for periodic task '{task.Title}': {task.Start:o} -> {task.End:o}");
    }

    var payload = new CreateTaskRequest
    {
        ProjectId = projectId,
        Title = task.Title,
        Content = CreatePeriodicTaskDescription(task.Identifier),
        StartDate = FormatTickTickDate(task.Start),
        DueDate = FormatTickTickDate(task.End),
        TimeZone = BucharestTimeZoneId,
        IsAllDay = false,
        Reminders = ["TRIGGER:PT0S"],
        Priority = 0,
        Tags = [PeriodicTaskCleanupTag]
    };

    using var content = JsonContent.Create(payload, options: options);
    using var response = await http.PostAsync($"{ApiBaseUrl}/task", content);
    var responseBody = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        throw new HttpRequestException($"Task create failed: {(int)response.StatusCode} {response.ReasonPhrase}. {responseBody}");
    }

    var createdTask = JsonSerializer.Deserialize<TickTickTask>(responseBody, options) ?? new TickTickTask();
    createdTask.ProjectId ??= projectId;
    createdTask.Title ??= task.Title;
    createdTask.Content ??= CreatePeriodicTaskDescription(task.Identifier);
    createdTask.StartDate ??= FormatTickTickDate(task.Start);
    createdTask.DueDate ??= FormatTickTickDate(task.End);
    createdTask.TimeZone ??= BucharestTimeZoneId;
    createdTask.Reminders ??= ["TRIGGER:PT0S"];
    createdTask.Tags ??= [PeriodicTaskCleanupTag];
    return createdTask;
}

static async Task<List<TaskCloseResult>> CloseOldTaggedTasksAsync(
    List<TaskWithProject> allTasks,
    string accessToken,
    DateTimeOffset now,
    bool simulate)
{
    var results = new List<TaskCloseResult>();

    foreach (var item in allTasks
        .Where(item => item.Task.Status != 2)
        .Where(item => HasCloseWhenOldTag(item.Task))
        .Where(item => TryGetTaskDueDate(item.Task, out var dueDate) && dueDate < now)
        .OrderBy(item => TryGetTaskDueDate(item.Task, out var dueDate) ? dueDate : DateTimeOffset.MaxValue)
        .ThenBy(item => item.Task.Title, StringComparer.CurrentCultureIgnoreCase))
    {
        try
        {
            if (!simulate)
            {
                await CompleteTaskAsync(item.Task, accessToken);
            }

            item.Task.Status = 2;
            results.Add(TaskCloseResult.SuccessResult(item));
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            results.Add(TaskCloseResult.Failure(item, ex.Message));
        }
    }

    return results;
}

static async Task CompleteTaskAsync(TickTickTask task, string accessToken)
{
    if (string.IsNullOrWhiteSpace(task.Id) || string.IsNullOrWhiteSpace(task.ProjectId))
    {
        throw new InvalidOperationException("The task has no id or projectId and cannot be completed.");
    }

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    var projectId = Uri.EscapeDataString(task.ProjectId);
    var taskId = Uri.EscapeDataString(task.Id);
    using var response = await http.PostAsync($"{ApiBaseUrl}/project/{projectId}/task/{taskId}/complete", content: null);
    var responseBody = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        throw new HttpRequestException($"Task complete failed: {(int)response.StatusCode} {response.ReasonPhrase}. {responseBody}");
    }
}

static string FormatTickTickDate(DateTimeOffset date)
{
    var offset = date.Offset;
    var sign = offset < TimeSpan.Zero ? "-" : "+";
    offset = offset.Duration();
    return $"{date:yyyy-MM-dd'T'HH:mm:ss.fff}{sign}{offset.Hours:00}{offset.Minutes:00}";
}

static async Task<List<TaskWithProject>> GetAllTasksAsync(string accessToken, JsonSerializerOptions options)
{
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    var projects = await GetProjectsAsync(http, options);
    var tasks = new List<TaskWithProject>();

    var inboxData = await GetProjectDataAsync(http, InboxProjectId, options);
    foreach (var task in inboxData.Tasks ?? [])
    {
        tasks.Add(new TaskWithProject(task, "Inbox"));
    }

    foreach (var project in projects)
    {
        if (string.IsNullOrWhiteSpace(project.Id))
        {
            continue;
        }

        var projectData = await GetProjectDataAsync(http, project.Id, options);
        foreach (var task in projectData.Tasks ?? [])
        {
            tasks.Add(new TaskWithProject(task, project.Name ?? project.Id));
        }
    }

    return tasks;
}

static void PrintTask(TaskWithProject item)
{
    var startText = TryParseTickTickDate(item.Task.StartDate, out var startDate)
        ? startDate.LocalDateTime.ToString("HH:mm", CultureInfo.CurrentCulture)
        : "--:--";
    var dueText = TryParseTickTickDate(item.Task.DueDate, out var dueDate)
        ? dueDate.LocalDateTime.ToString("HH:mm", CultureInfo.CurrentCulture)
        : "--:--";
    var priority = FormatPriority(item.Task.Priority);
    var tags = FormatTags(item.Task.Tags);

    Console.Write($"  - [{startText} -> {dueText}] ");
    WriteColored(TruncateTaskTitle(item.Task.Title), ConsoleColor.Green);
    Console.WriteLine($" ({item.ProjectName}){priority}{tags}");
}

static bool HasDoNotMoveTag(TickTickTask task)
{
    return HasTag(task, "donotmove");
}

static bool HasCloseWhenOldTag(TickTickTask task)
{
    return HasTag(task, "closewhenold");
}

static bool HasTag(TickTickTask task, string expectedName)
{
    return task.Tags?.Any(tag => IsTagNamed(tag, expectedName)) == true;
}

static bool IsTagNamed(string? tag, string expectedName)
{
    if (string.IsNullOrWhiteSpace(tag))
    {
        return false;
    }

    var normalizedTag = tag.Trim().TrimStart('#');
    return string.Equals(normalizedTag, expectedName, StringComparison.OrdinalIgnoreCase);
}

static string FormatTags(List<string>? tags)
{
    if (tags is null || tags.Count == 0)
    {
        return "";
    }

    var formattedTags = tags
        .Where(tag => !string.IsNullOrWhiteSpace(tag))
        .Select(tag => tag.Trim().StartsWith('#') ? tag.Trim() : $"#{tag.Trim()}");

    return $" [{string.Join(' ', formattedTags)}]";
}

static string TruncateTaskTitle(string? title)
{
    if (string.IsNullOrEmpty(title) || title.Length <= MaxDisplayedTaskTitleLength)
    {
        return title ?? "";
    }

    return title[..MaxDisplayedTaskTitleLength];
}

static void WriteColored(string text, ConsoleColor color)
{
    var originalColor = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.Write(text);
    Console.ForegroundColor = originalColor;
}

static void WriteColoredLine(string text, ConsoleColor color)
{
    WriteColored(text, color);
    Console.WriteLine();
}

static async Task<TokenResponse> AuthorizeAsync(
    HttpClient http,
    TokenStore tokenStore,
    string clientId,
    string clientSecret,
    string redirectUri,
    JsonSerializerOptions options)
{
    var state = Guid.NewGuid().ToString("N");
    var authorizationUrl = QueryHelpers.BuildUrl(AuthorizeUrl, new Dictionary<string, string>
    {
        ["client_id"] = clientId,
        ["redirect_uri"] = redirectUri,
        ["scope"] = Scope,
        ["state"] = state,
        ["response_type"] = "code"
    });

    using var redirectListener = StartOAuthRedirectListener(redirectUri);

    Console.WriteLine("The authorization URL was opened in your browser:");
    OpenBrowser(authorizationUrl);
    Console.WriteLine(authorizationUrl);
    Console.WriteLine();

    var code = redirectListener is null
        ? null
        : await WaitForOAuthCodeAsync(redirectListener, state);

    if (string.IsNullOrWhiteSpace(code))
    {
        Console.WriteLine("After authorizing, paste the full redirect URL or only the code parameter value:");
        var input = Console.ReadLine();
        code = ExtractCode(input);
    }

    if (string.IsNullOrWhiteSpace(code))
    {
        throw new InvalidOperationException("The OAuth code could not be read.");
    }

    var tokenUrl = QueryHelpers.BuildUrl(TokenUrl, new Dictionary<string, string>
    {
        ["client_id"] = clientId,
        ["client_secret"] = clientSecret,
        ["code"] = code,
        ["grant_type"] = "authorization_code",
        ["scope"] = Scope,
        ["redirect_uri"] = redirectUri
    });

    using var response = await http.PostAsync(tokenUrl, content: null);
    var body = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        throw new HttpRequestException($"Token request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
    }

    var token = JsonSerializer.Deserialize<TokenResponse>(body, options)
        ?? throw new InvalidOperationException("The OAuth response does not contain a token.");

    token.ReceivedAtUtc = DateTimeOffset.UtcNow;
    await tokenStore.SaveAsync(token);
    return token;
}

static HttpListener? StartOAuthRedirectListener(string redirectUri)
{
    if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri) || !uri.IsLoopback)
    {
        Console.WriteLine("The redirect URI is not local; falling back to manual OAuth code entry.");
        return null;
    }

    var prefix = redirectUri.EndsWith('/') ? redirectUri : $"{redirectUri}/";
    var listener = new HttpListener();
    listener.Prefixes.Add(prefix);

    try
    {
        listener.Start();
        Console.WriteLine($"Listening for the OAuth redirect at {prefix}");
        return listener;
    }
    catch (HttpListenerException ex)
    {
        listener.Close();
        Console.WriteLine($"Could not start the OAuth listener at {prefix}: {ex.Message}");
        return null;
    }
}

static async Task<string?> WaitForOAuthCodeAsync(HttpListener listener, string expectedState)
{
    var contextTask = listener.GetContextAsync();
    var completedTask = await Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes(5)));
    if (completedTask != contextTask)
    {
        Console.WriteLine("No OAuth redirect was received within five minutes; falling back to manual entry.");
        return null;
    }

    var context = await contextTask;
    var parameters = ParseQuery(context.Request.Url?.Query);
    var responseText = "Authorization received. You can close this tab and return to the application.";

    if (parameters.TryGetValue("error", out var error))
    {
        responseText = $"Authorization failed: {WebUtility.HtmlEncode(error)}";
        await WriteOAuthBrowserResponseAsync(context, responseText);
        throw new InvalidOperationException($"OAuth error: {error}");
    }

    if (!parameters.TryGetValue("state", out var actualState) || actualState != expectedState)
    {
        await WriteOAuthBrowserResponseAsync(context, "Authorization failed: invalid OAuth state.");
        throw new InvalidOperationException("Invalid OAuth state.");
    }

    parameters.TryGetValue("code", out var code);
    await WriteOAuthBrowserResponseAsync(context, responseText);
    return code;
}

static async Task WriteOAuthBrowserResponseAsync(HttpListenerContext context, string message)
{
    var html = $"""
        <!doctype html>
        <html>
        <head><meta charset="utf-8"><title>TickTickToday OAuth</title></head>
        <body><p>{WebUtility.HtmlEncode(message)}</p></body>
        </html>
        """;
    var bytes = System.Text.Encoding.UTF8.GetBytes(html);
    context.Response.ContentType = "text/html; charset=utf-8";
    context.Response.ContentLength64 = bytes.Length;
    await context.Response.OutputStream.WriteAsync(bytes);
    context.Response.Close();
}

static Dictionary<string, string> ParseQuery(string? query)
{
    return (query ?? "")
        .TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .Where(parts => parts.Length == 2)
        .ToDictionary(
            parts => Uri.UnescapeDataString(parts[0]),
            parts => Uri.UnescapeDataString(parts[1].Replace("+", " ")),
            StringComparer.OrdinalIgnoreCase);
}

static void OpenBrowser(string url)
{
    try
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }
    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
    {
        Console.WriteLine($"The browser could not be opened automatically: {ex.Message}");
    }
}

static async Task<List<Project>> GetProjectsAsync(HttpClient http, JsonSerializerOptions options)
{
    using var response = await http.GetAsync($"{ApiBaseUrl}/project");
    response.EnsureSuccessStatusCode();

    await using var stream = await response.Content.ReadAsStreamAsync();
    return await JsonSerializer.DeserializeAsync<List<Project>>(stream, options) ?? [];
}

static async Task<ProjectData> GetProjectDataAsync(HttpClient http, string projectId, JsonSerializerOptions options)
{
    using var response = await http.GetAsync($"{ApiBaseUrl}/project/{Uri.EscapeDataString(projectId)}/data");
    response.EnsureSuccessStatusCode();

    await using var stream = await response.Content.ReadAsStreamAsync();
    return await JsonSerializer.DeserializeAsync<ProjectData>(stream, options) ?? new ProjectData();
}

static string? ExtractCode(string? input)
{
    if (string.IsNullOrWhiteSpace(input))
    {
        return null;
    }

    if (Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri))
    {
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in query)
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length == 2 && pieces[0] == "code")
            {
                return Uri.UnescapeDataString(pieces[1]);
            }
        }
    }

    return input.Trim();
}

static bool TryParseTickTickDate(string? value, out DateTimeOffset date)
{
    if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date))
    {
        return true;
    }

    date = default;
    return false;
}

static bool TryGetTaskDueDate(TickTickTask task, out DateTimeOffset date)
{
    return TryParseTickTickDate(task.DueDate, out date);
}

static string FormatPriority(int priority) => priority switch
{
    1 => " [low]",
    3 => " [medium]",
    5 => " [high]",
    _ => ""
};

sealed class TokenStore(string path, JsonSerializerOptions options)
{
    public async Task<TokenResponse?> LoadAsync()
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<TokenResponse>(stream, options);
    }

    public async Task SaveAsync(TokenResponse token)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, token, options);
    }
}

sealed class TokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; set; }

    public DateTimeOffset ReceivedAtUtc { get; set; }

    [JsonIgnore]
    public bool IsExpired =>
        ExpiresIn is > 0 &&
        ReceivedAtUtc.AddSeconds(ExpiresIn.Value - 60) <= DateTimeOffset.UtcNow;
}

sealed class Project
{
    public string? Id { get; set; }

    public string? Name { get; set; }
}

sealed class ProjectData
{
    public List<TickTickTask>? Tasks { get; set; }
}

sealed class TickTickTask
{
    public string? Id { get; set; }

    public string? ProjectId { get; set; }

    public string? Title { get; set; }

    public string? Content { get; set; }

    public string? StartDate { get; set; }

    public string? DueDate { get; set; }

    public string? TimeZone { get; set; }

    public bool IsAllDay { get; set; }

    public List<string>? Reminders { get; set; }

    public int Status { get; set; }

    public int Priority { get; set; }

    public string? RepeatFlag { get; set; }

    public List<string>? Tags { get; set; }
}

sealed class UpdateTaskRequest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("projectId")]
    public string ProjectId { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("startDate")]
    public string StartDate { get; set; } = "";

    [JsonPropertyName("dueDate")]
    public string DueDate { get; set; } = "";

    [JsonPropertyName("timeZone")]
    public string TimeZone { get; set; } = "";

    [JsonPropertyName("isAllDay")]
    public bool IsAllDay { get; set; }

    [JsonPropertyName("reminders")]
    public List<string> Reminders { get; set; } = [];

    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];
}

sealed class CreateTaskRequest
{
    [JsonPropertyName("projectId")]
    public string ProjectId { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("startDate")]
    public string StartDate { get; set; } = "";

    [JsonPropertyName("dueDate")]
    public string DueDate { get; set; } = "";

    [JsonPropertyName("timeZone")]
    public string TimeZone { get; set; } = "";

    [JsonPropertyName("isAllDay")]
    public bool IsAllDay { get; set; }

    [JsonPropertyName("reminders")]
    public List<string> Reminders { get; set; } = [];

    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];
}

sealed record TaskWithProject(TickTickTask Task, string ProjectName);

sealed record TaskInterval(DateTimeOffset Start, DateTimeOffset End);

sealed record PeriodicTaskRule(CronSchedule Schedule, TimeSpan Duration, string Title, string ListName, string Identifier);

sealed record PeriodicTaskOccurrence(string Title, string ListName, string Identifier, DateTimeOffset Start, DateTimeOffset End);

sealed record PeriodicTaskCreateResult(
    bool Success,
    bool Created,
    string Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    string Message)
{
    public static PeriodicTaskCreateResult CreatedResult(string title, DateTimeOffset start, DateTimeOffset end) =>
        new(true, true, title, start, end, "");

    public static PeriodicTaskCreateResult ExistingResult(string title, DateTimeOffset start, DateTimeOffset end) =>
        new(true, false, title, start, end, "");

    public static PeriodicTaskCreateResult Failure(string title, DateTimeOffset start, DateTimeOffset end, string message) =>
        new(false, false, title, start, end, message);
}

static class PeriodicTaskSchedule
{
    public static List<PeriodicTaskRule> Load(string fileName)
    {
        var path = ResolvePath(fileName);
        if (!File.Exists(path))
        {
            return [];
        }

        var rules = new List<PeriodicTaskRule>();
        var document = XDocument.Load(path, LoadOptions.SetLineInfo);
        var root = document.Root;

        if (root is null || !string.Equals(root.Name.LocalName, "periodicTasks", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{path}: the XML root must be <periodicTasks>.");
        }

        foreach (var element in root.Elements())
        {
            if (!string.Equals(element.Name.LocalName, "task", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"{FormatXmlLocation(path, element)}: unknown element <{element.Name.LocalName}>. Use <task />.");
            }

            var rule = ParseTaskElement(path, element);
            if (rules.Any(existing => string.Equals(existing.Identifier, rule.Identifier, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"{FormatXmlLocation(path, element)}: periodic identifier '{rule.Identifier}' is duplicated.");
            }

            rules.Add(rule);
        }

        return rules;
    }

    private static string ResolvePath(string fileName)
    {
        var currentDirectoryPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), fileName));
        if (File.Exists(currentDirectoryPath))
        {
            return currentDirectoryPath;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, fileName));
    }

    private static PeriodicTaskRule ParseTaskElement(string path, XElement element)
    {
        var location = FormatXmlLocation(path, element);
        var title = ReadRequiredAttribute(element, "title", location);
        var identifier = ReadRequiredAttribute(element, "id", location);
        var listName = element.Attribute("list")?.Value.Trim();
        listName = string.IsNullOrWhiteSpace(listName) ? "Inbox" : listName;
        var cron = element.Attribute("cron")?.Value.Trim();

        ValidateAllowedAttributes(element, location, ["cron", "duration", "day", "start", "end", "title", "list", "id"]);

        if (identifier.Length is < 3 or > 32 ||
            !identifier.All(character => char.IsLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new InvalidOperationException($"{location}: invalid identifier '{identifier}'. Use 3-32 letters, digits, hyphens, or underscores.");
        }

        if (!string.IsNullOrWhiteSpace(cron))
        {
            if (element.Attribute("day") is not null || element.Attribute("start") is not null || element.Attribute("end") is not null)
            {
                throw new InvalidOperationException($"{location}: do not combine 'cron' with the legacy 'day', 'start', or 'end' attributes.");
            }

            var durationText = ReadRequiredAttribute(element, "duration", location);
            if (!TryParseDuration(durationText, out var duration))
            {
                throw new InvalidOperationException($"{location}: invalid duration '{durationText}'. Use HH:mm between 00:01 and 24:00.");
            }

            return new PeriodicTaskRule(CronSchedule.Parse(cron, location), duration, title, listName, identifier);
        }

        // Compatibility with rules created before cron expressions were introduced.
        var day = ReadRequiredAttribute(element, "day", location);
        var start = ReadRequiredAttribute(element, "start", location);
        var end = ReadRequiredAttribute(element, "end", location);

        if (element.Attribute("duration") is not null)
        {
            throw new InvalidOperationException($"{location}: the 'duration' attribute can only be used with 'cron'.");
        }

        if (!TryParseDayOfWeek(day, out var dayOfWeek))
        {
            throw new InvalidOperationException($"{location}: invalid day '{day}'. Use an English day name, abbreviation, or a number from 0 to 7.");
        }

        if (!TryParseTime(start, out var startTime) || !TryParseTime(end, out var endTime))
        {
            throw new InvalidOperationException($"{location}: invalid time. Use 18, 18:00, or 18.30.");
        }

        if (endTime <= startTime)
        {
            throw new InvalidOperationException($"{location}: the end time must be after the start time.");
        }

        var cronExpression = $"{startTime.Minute} {startTime.Hour} * * {(int)dayOfWeek}";
        return new PeriodicTaskRule(
            CronSchedule.Parse(cronExpression, location),
            endTime - startTime,
            title,
            listName,
            identifier);
    }

    private static string ReadRequiredAttribute(XElement element, string name, string location)
    {
        var value = element.Attribute(name)?.Value.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{location}: required attribute '{name}' is missing.");
        }

        return value;
    }

    private static void ValidateAllowedAttributes(XElement element, string location, IReadOnlyCollection<string> allowedAttributeNames)
    {
        foreach (var attribute in element.Attributes())
        {
            if (!allowedAttributeNames.Any(name => string.Equals(name, attribute.Name.LocalName, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"{location}: unknown attribute '{attribute.Name.LocalName}'.");
            }
        }
    }

    private static string FormatXmlLocation(string path, XElement element)
    {
        var lineInfo = (IXmlLineInfo)element;
        return lineInfo.HasLineInfo()
            ? $"{path}, line {lineInfo.LineNumber}"
            : path;
    }

    private static bool TryParseDayOfWeek(string value, out DayOfWeek dayOfWeek)
    {
        var normalized = value.Trim();
        if (int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var numericDay) &&
            numericDay is >= 0 and <= 7)
        {
            dayOfWeek = numericDay == 7 ? DayOfWeek.Sunday : (DayOfWeek)numericDay;
            return true;
        }

        dayOfWeek = normalized.ToLowerInvariant() switch
        {
            "mon" or "monday" => DayOfWeek.Monday,
            "tue" or "tues" or "tuesday" => DayOfWeek.Tuesday,
            "wed" or "wednesday" => DayOfWeek.Wednesday,
            "thu" or "thur" or "thurs" or "thursday" => DayOfWeek.Thursday,
            "fri" or "friday" => DayOfWeek.Friday,
            "sat" or "saturday" => DayOfWeek.Saturday,
            "sun" or "sunday" => DayOfWeek.Sunday,
            _ => (DayOfWeek)(-1)
        };

        return dayOfWeek != (DayOfWeek)(-1);
    }

    private static bool TryParseTime(string value, out TimeOnly time)
    {
        var normalized = value.Replace('.', ':');
        if (!normalized.Contains(':'))
        {
            normalized = $"{normalized}:00";
        }

        return TimeOnly.TryParseExact(
            normalized,
            ["H:mm", "HH:mm"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out time);
    }

    private static bool TryParseDuration(string value, out TimeSpan duration)
    {
        duration = default;
        var parts = value.Trim().Split(':');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            hours < 0 || minutes is < 0 or > 59)
        {
            return false;
        }

        if (hours > 24 || (hours == 24 && minutes != 0))
        {
            return false;
        }

        duration = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes);
        return duration > TimeSpan.Zero;
    }
}

sealed class CronSchedule
{
    private readonly CronExpression _expression;

    private CronSchedule(CronExpression expression) => _expression = expression;

    public static CronSchedule Parse(string expression, string location)
    {
        try
        {
            return new CronSchedule(CronExpression.Parse(expression, CronFormat.Standard));
        }
        catch (Exception ex) when (ex is CronFormatException or MissingSeedException)
        {
            throw new InvalidOperationException($"{location}: invalid cron expression '{expression}': {ex.Message}", ex);
        }
    }

    public IEnumerable<DateTimeOffset> GetOccurrences(DateTime day, TimeZoneInfo timeZone)
    {
        var localStart = DateTime.SpecifyKind(day.Date, DateTimeKind.Unspecified);
        var localEnd = localStart.AddDays(1);
        var from = new DateTimeOffset(localStart, timeZone.GetUtcOffset(localStart));
        var to = new DateTimeOffset(localEnd, timeZone.GetUtcOffset(localEnd));
        return _expression.GetOccurrences(from, to, timeZone, fromInclusive: true, toInclusive: false);
    }
}

sealed record AppSettings(
    RunOptions Options,
    string? RedirectUri,
    string PeriodicTasksFile)
{
    private const string DefaultPeriodicTasksFileName = "periodic-tasks.xml";

    public static AppSettings Load(
        string requestedIniPath,
        int defaultLookbackDays,
        int defaultPlanDays,
        int defaultPeriodicDays)
    {
        var iniPath = ResolveIniPath(requestedIniPath);
        var iniValues = IniFile.Load(iniPath);
        ValidateIniKeys(iniValues);

        return new AppSettings(
            RunOptions.Parse(iniValues, defaultLookbackDays, defaultPlanDays, defaultPeriodicDays),
            GetIniString(iniValues, "redirect-uri"),
            GetIniString(iniValues, "periodic-tasks-file") ?? DefaultPeriodicTasksFileName);
    }

    private static void ValidateIniKeys(Dictionary<string, string> values)
    {
        var allowedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "redirect-uri",
            "periodic-tasks-file",
            "move",
            "plan-days",
            "lookback-days",
            "day-start",
            "day-end",
            "periodic-days",
            "close-when-old",
            "login",
            "simulate",
            "check-config"
        };

        foreach (var key in values.Keys)
        {
            if (!allowedKeys.Contains(key))
            {
                throw new InvalidOperationException($"Unknown INI key: '{key}'.");
            }
        }
    }

    private static string ResolveIniPath(string requestedIniPath)
    {
        if (string.IsNullOrWhiteSpace(requestedIniPath))
        {
            throw new InvalidOperationException("The INI file path cannot be empty.");
        }

        var path = Path.GetFullPath(requestedIniPath);
        if (File.Exists(path))
        {
            return path;
        }

        throw new FileNotFoundException($"INI file was not found: {path}", path);
    }

    private static string? GetIniString(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
}

static class EnvironmentFile
{
    public static Dictionary<string, string> LoadOptional(string fileName)
    {
        var path = ResolvePath(fileName);
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lineNumber = 0;

        foreach (var rawLine in File.ReadLines(path))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
            {
                line = line["export ".Length..].TrimStart();
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                throw new InvalidOperationException($"Invalid environment line in {path}, line {lineNumber}: use NAME=value.");
            }

            var key = line[..separatorIndex].Trim();
            if (!IsValidKey(key))
            {
                throw new InvalidOperationException($"Invalid environment key '{key}' in {path}, line {lineNumber}.");
            }

            values[key] = Unquote(line[(separatorIndex + 1)..].Trim());
        }

        return values;
    }

    private static string ResolvePath(string fileName)
    {
        var currentDirectoryPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), fileName));
        if (File.Exists(currentDirectoryPath))
        {
            return currentDirectoryPath;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, fileName));
    }

    private static bool IsValidKey(string key) =>
        key.Length > 0 &&
        (char.IsLetter(key[0]) || key[0] == '_') &&
        key.All(character => char.IsLetterOrDigit(character) || character == '_');

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value.StartsWith('"') && value.EndsWith('"')) ||
             (value.StartsWith('\'') && value.EndsWith('\''))))
        {
            return value[1..^1];
        }

        return value;
    }
}

static class IniFile
{
    public static Dictionary<string, string> Load(string path)
    {
        var fullPath = ResolvePath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"The INI file does not exist: {fullPath}", fullPath);
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lineNumber = 0;

        foreach (var rawLine in File.ReadLines(fullPath))
        {
            lineNumber++;
            var line = rawLine.Trim();

            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                throw new InvalidOperationException($"Invalid INI line in {fullPath}, line {lineNumber}: use key=value.");
            }

            var key = line[..separatorIndex].Trim();
            var value = StripInlineComment(line[(separatorIndex + 1)..]).Trim();

            if (key.Length == 0)
            {
                throw new InvalidOperationException($"Empty INI key in {fullPath}, line {lineNumber}.");
            }

            values[key] = Unquote(value);
        }

        return values;
    }

    private static string ResolvePath(string path)
    {
        var trimmedPath = path.Trim().Trim('"');
        if (Path.IsPathFullyQualified(trimmedPath))
        {
            return trimmedPath;
        }

        var currentDirectoryPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), trimmedPath));
        if (File.Exists(currentDirectoryPath))
        {
            return currentDirectoryPath;
        }

        var appDirectoryPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, trimmedPath));
        return File.Exists(appDirectoryPath) ? appDirectoryPath : currentDirectoryPath;
    }

    private static string StripInlineComment(string value)
    {
        var inQuotes = false;

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && (character == ';' || character == '#'))
            {
                return value[..index];
            }
        }

        return value;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
        {
            return value[1..^1];
        }

        return value;
    }
}

sealed record RunOptions(
    int? MoveLimit,
    int PlanDays,
    int LookbackDays,
    TimeOnly DayStart,
    TimeOnly DayEnd,
    int PeriodicDays,
    bool CloseWhenOld,
    bool ForceLogin,
    bool Simulate,
    bool CheckConfig)
{
    private static readonly TimeOnly DefaultDayStart = new(8, 0);
    private static readonly TimeOnly DefaultDayEnd = new(22, 0);

    public static RunOptions Parse(
        IReadOnlyDictionary<string, string> values,
        int defaultLookbackDays,
        int defaultPlanDays,
        int defaultPeriodicDays)
    {
        var dayStart = ParseTime(values, "day-start", DefaultDayStart);
        var dayEnd = ParseTime(values, "day-end", DefaultDayEnd);
        if (dayStart >= dayEnd)
        {
            throw new InvalidOperationException("Invalid INI planning window: 'day-start' must be earlier than 'day-end'.");
        }

        return new RunOptions(
            ParseOptionalNonNegativeInt(values, "move"),
            ParsePositiveInt(values, "plan-days", defaultPlanDays),
            ParsePositiveInt(values, "lookback-days", defaultLookbackDays),
            dayStart,
            dayEnd,
            ParseNonNegativeInt(values, "periodic-days", defaultPeriodicDays),
            ParseBool(values, "close-when-old", defaultValue: true),
            ParseBool(values, "login", defaultValue: false),
            ParseBool(values, "simulate", defaultValue: false),
            ParseBool(values, "check-config", defaultValue: false));
    }

    private static int? ParseOptionalNonNegativeInt(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "null", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "unlimited", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : throw new InvalidOperationException($"Invalid INI value for '{key}': use an integer >= 0 or 'unlimited'.");
    }

    private static int ParsePositiveInt(IReadOnlyDictionary<string, string> values, string key, int defaultValue)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new InvalidOperationException($"Invalid INI value for '{key}': use an integer > 0.");
    }

    private static int ParseNonNegativeInt(IReadOnlyDictionary<string, string> values, string key, int defaultValue)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : throw new InvalidOperationException($"Invalid INI value for '{key}': use an integer >= 0.");
    }

    private static TimeOnly ParseTime(
        IReadOnlyDictionary<string, string> values,
        string key,
        TimeOnly defaultValue)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return TimeOnly.TryParseExact(
            value.Trim(),
            "HH:mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Invalid INI value for '{key}': use HH:mm (for example, 08:00).");
    }

    private static bool ParseBool(IReadOnlyDictionary<string, string> values, string key, bool defaultValue)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "y" => true,
            "0" or "false" or "no" or "n" => false,
            _ => throw new InvalidOperationException($"Invalid INI value for '{key}': use 1/0, true/false, or yes/no.")
        };
    }

}

sealed record TaskMoveResult(
    bool Success,
    TaskWithProject Task,
    DateTimeOffset NewStart,
    DateTimeOffset NewDue,
    string Message)
{
    public static TaskMoveResult SuccessResult(TaskWithProject task, DateTimeOffset newStart, DateTimeOffset newDue) =>
        new(true, task, newStart, newDue, "");

    public static TaskMoveResult Failure(TaskWithProject task, string message) =>
        new(false, task, default, default, message);
}

sealed record TaskCloseResult(
    bool Success,
    TaskWithProject Task,
    string Message)
{
    public static TaskCloseResult SuccessResult(TaskWithProject task) =>
        new(true, task, "");

    public static TaskCloseResult Failure(TaskWithProject task, string message) =>
        new(false, task, message);
}

static class QueryHelpers
{
    public static string BuildUrl(string baseUrl, IReadOnlyDictionary<string, string> parameters)
    {
        var query = string.Join("&", parameters.Select(item =>
            $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"));

        return $"{baseUrl}?{query}";
    }
}
