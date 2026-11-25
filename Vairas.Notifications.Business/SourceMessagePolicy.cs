using System.Security.Cryptography;
using System.Text.Json;

namespace Vairas.Notifications.Business;

public static class SourceMessagePolicy
{
    private static readonly string[] Scheduling = ["LessonScheduled", "LessonRescheduled", "LessonCancelled", "LessonCompleted", "LessonNoShow", "LessonParticipantsChanged"];
    private static readonly string[] Learning = ["AssessmentPassed", "AssessmentFailed", "AssessmentResultRevised"];

    public static string ValidateAndHash(SourceMessage message)
    {
        if (message.EventId == Guid.Empty || message.SchoolId == Guid.Empty || message.SourceId == Guid.Empty || message.SourceRevision < 1
            || message.EventVersion != 1 || message.OccurredAt == default || message.OccurredAt.Offset != TimeSpan.Zero
            || message.Payload.Length > 65536 || message.CorrelationId.Length is < 1 or > 128
            || message.CorrelationId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new BadRequestException("INVALID_EVENT");
        var allowed = message.Producer switch { "scheduling" => Scheduling, "learning" => Learning, _ => [] };
        if (!allowed.Contains(message.EventType, StringComparer.Ordinal)) throw new BadRequestException("INVALID_EVENT_TYPE");
        using var document = JsonDocument.Parse(message.Payload, new JsonDocumentOptions { MaxDepth = 16 });
        var payload = document.RootElement;
        var isLesson = message.Producer == "scheduling";
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("schoolId", out var school) || school.ValueKind != JsonValueKind.String || !school.TryGetGuid(out var schoolId) || schoolId != message.SchoolId
            || !payload.TryGetProperty(isLesson ? "lessonId" : "assessmentId", out var source) || source.ValueKind != JsonValueKind.String || !source.TryGetGuid(out var sourceId) || sourceId != message.SourceId)
            throw new BadRequestException("EVENT_BINDING_MISMATCH");
        var allowedFields = isLesson ? new[] { "schoolId", "lessonId", "lessonKind", "teacherId", "enrollmentId", "groupId", "vehicleId", "startsAt", "endsAt", "previousStartsAt", "previousEndsAt", "cancelledAt", "groupMembershipRevision", "scheduledDurationMinutes" }
            : ["schoolId", "assessmentId", "enrollmentId", "assessedAt", "version", "status"];
        foreach (var property in payload.EnumerateObject())
            if (!allowedFields.Contains(property.Name, StringComparer.Ordinal)) throw new BadRequestException("UNEXPECTED_EVENT_FIELD");
        if (isLesson)
        {
            var kind = RequiredString(payload, "lessonKind");
            RequiredId(payload, "teacherId");
            if (kind == "Practical") { RequiredId(payload, "enrollmentId"); RequiredId(payload, "vehicleId"); }
            else if (kind == "Theory") RequiredId(payload, "groupId");
            else throw new BadRequestException("INVALID_LESSON_KIND");
            if (message.EventType is "LessonScheduled" or "LessonRescheduled" or "LessonCompleted" or "LessonNoShow")
                Interval(payload, "startsAt", "endsAt");
            if (message.EventType == "LessonRescheduled") Interval(payload, "previousStartsAt", "previousEndsAt");
            if (message.EventType == "LessonCancelled") RequiredTime(payload, "cancelledAt");
            if (message.EventType == "LessonCompleted") PositiveInteger(payload, "scheduledDurationMinutes");
            if (message.EventType == "LessonParticipantsChanged")
            { if (kind != "Theory") throw new BadRequestException("INVALID_ROSTER_EVENT"); PositiveInteger(payload, "groupMembershipRevision"); }
        }
        else
        {
            RequiredId(payload, "enrollmentId");
            if (message.EventType == "AssessmentResultRevised")
            {
                PositiveInteger(payload, "version");
                if (RequiredString(payload, "status") is not ("Recorded" or "Annulled" or "ReviewRequired")) throw new BadRequestException("INVALID_ASSESSMENT_REVISION");
            }
            else RequiredTime(payload, "assessedAt");
        }
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("producer", message.Producer); writer.WriteString("type", message.EventType);
            writer.WriteNumber("version", message.EventVersion); writer.WriteNumber("revision", message.SourceRevision); writer.WriteString("occurredAt", message.OccurredAt);
            writer.WriteString("correlationId", message.CorrelationId); writer.WritePropertyName("payload"); Canonical(writer, payload); writer.WriteEndObject();
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    public static string Context(string producer) => producer switch { "scheduling" => "Scheduling", "learning" => "LearningTracking", _ => throw new BadRequestException("INVALID_PRODUCER") };
    public static string Producer(string context) => context == "Scheduling" ? "scheduling" : context == "LearningTracking" ? "learning" : throw new BadRequestException("INVALID_SOURCE");
    private static string RequiredString(JsonElement value, string name) => value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : throw new BadRequestException("INVALID_EVENT_FIELD");
    private static void RequiredId(JsonElement value, string name)
    { if (!value.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String || !p.TryGetGuid(out var id) || id == Guid.Empty) throw new BadRequestException("INVALID_EVENT_FIELD"); }
    private static DateTimeOffset RequiredTime(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String || !p.TryGetDateTimeOffset(out var time) || time == default || time.Offset != TimeSpan.Zero) throw new BadRequestException("INVALID_EVENT_TIME");
        return time;
    }
    private static void Interval(JsonElement value, string start, string end)
    { if (RequiredTime(value, start) >= RequiredTime(value, end)) throw new BadRequestException("INVALID_EVENT_INTERVAL"); }
    private static void PositiveInteger(JsonElement value, string name)
    { if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var revision) || revision < 1) throw new BadRequestException("INVALID_EVENT_REVISION"); }
    private static void Canonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject(); var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            { if (!names.Add(property.Name)) throw new BadRequestException("DUPLICATE_EVENT_FIELD"); writer.WritePropertyName(property.Name); Canonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array) { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Canonical(writer, item); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }
}
