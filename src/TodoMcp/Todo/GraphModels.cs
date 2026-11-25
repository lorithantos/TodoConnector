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

    [JsonIgnore] public bool IsCompleted => string.Equals(this.Status, "completed", StringComparison.OrdinalIgnoreCase);
}
