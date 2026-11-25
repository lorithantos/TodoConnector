using System.Text.Json.Serialization;

namespace TodoMcp.Todo;

// Shapes of the Microsoft Graph To Do API (v1.0). Only the fields this server uses.

public sealed class GraphPage<T>
{
    [JsonPropertyName("value")] public List<T> Value { get; set; } = [];
    [JsonPropertyName("@odata.nextLink")] public string? NextLink { get; set; }
}

public sealed class GraphTaskList
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("wellknownListName")] public string? WellknownListName { get; set; }
    [JsonPropertyName("isShared")] public bool IsShared { get; set; }
}

public sealed class GraphDateTimeTimeZone
{
    [JsonPropertyName("dateTime")] public string DateTime { get; set; } = "";
    [JsonPropertyName("timeZone")] public string TimeZone { get; set; } = "UTC";
}

public sealed class GraphItemBody
{
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("contentType")] public string ContentType { get; set; } = "text";
}

public sealed class GraphTask
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "notStarted";
    [JsonPropertyName("importance")] public string Importance { get; set; } = "normal";
    [JsonPropertyName("isReminderOn")] public bool IsReminderOn { get; set; }
    [JsonPropertyName("body")] public GraphItemBody? Body { get; set; }
    [JsonPropertyName("dueDateTime")] public GraphDateTimeTimeZone? DueDateTime { get; set; }
    [JsonPropertyName("reminderDateTime")] public GraphDateTimeTimeZone? ReminderDateTime { get; set; }
    [JsonPropertyName("completedDateTime")] public GraphDateTimeTimeZone? CompletedDateTime { get; set; }
    [JsonPropertyName("createdDateTime")] public DateTimeOffset? CreatedDateTime { get; set; }
    [JsonPropertyName("categories")] public List<string>? Categories { get; set; }
    [JsonPropertyName("recurrence")] public GraphRecurrence? Recurrence { get; set; }

    [JsonIgnore] public bool IsCompleted => string.Equals(this.Status, "completed", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A step of a task: https://learn.microsoft.com/graph/api/resources/checklistitem?view=graph-rest-1.0</summary>
public sealed class GraphChecklistItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("isChecked")] public bool IsChecked { get; set; }
}

/// <summary>
/// todoTask.recurrence, a patternedRecurrence:
/// https://learn.microsoft.com/graph/api/resources/patternedrecurrence?view=graph-rest-1.0.
/// Optional members are left out when null, because recurrencePattern's docs say a property
/// without a supported value is an error, and the PATCH serializer keeps nulls on purpose.
/// </summary>
public sealed class GraphRecurrence
{
    [JsonPropertyName("pattern")] public GraphRecurrencePattern Pattern { get; set; } = new();
    [JsonPropertyName("range")] public GraphRecurrenceRange Range { get; set; } = new();
}

/// <summary>https://learn.microsoft.com/graph/api/resources/recurrencepattern?view=graph-rest-1.0</summary>
public sealed class GraphRecurrencePattern
{
    [JsonPropertyName("type")] public string Type { get; set; } = "daily";
    [JsonPropertyName("interval")] public int Interval { get; set; } = 1;

    [JsonPropertyName("daysOfWeek"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? DaysOfWeek { get; set; }

    [JsonPropertyName("firstDayOfWeek"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FirstDayOfWeek { get; set; }

    [JsonPropertyName("dayOfMonth"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DayOfMonth { get; set; }

    [JsonPropertyName("month"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Month { get; set; }

    [JsonPropertyName("index"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Index { get; set; }
}

/// <summary>https://learn.microsoft.com/graph/api/resources/recurrencerange?view=graph-rest-1.0</summary>
public sealed class GraphRecurrenceRange
{
    [JsonPropertyName("type")] public string Type { get; set; } = "noEnd";
    [JsonPropertyName("startDate")] public string StartDate { get; set; } = "";

    [JsonPropertyName("endDate"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EndDate { get; set; }

    [JsonPropertyName("numberOfOccurrences"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NumberOfOccurrences { get; set; }

    [JsonPropertyName("recurrenceTimeZone"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecurrenceTimeZone { get; set; }
}
