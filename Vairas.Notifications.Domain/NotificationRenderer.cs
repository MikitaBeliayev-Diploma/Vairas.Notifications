using System.Net;
using System.Text.RegularExpressions;

namespace Vairas.Notifications.Domain;

/// <summary>Allowlisted text substitution. Values are encoded once and never evaluated as template code.</summary>
public static partial class NotificationRenderer
{
    private static readonly string[] Common = ["schoolName", "appLink"];
    private static readonly string[] Lesson = ["startsAt", "endsAt"];

    [GeneratedRegex(@"\{\{([A-Za-z][A-Za-z0-9]*)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    public static void Validate(NotificationType type, string subject, string body)
    {
        if (!Enum.IsDefined(type) || string.IsNullOrWhiteSpace(subject) || subject.Length > 500 || subject.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(body) || body.Length > 20000 || body.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new NotificationRuleException("INVALID_TEMPLATE");
        foreach (var text in new[] { subject, body })
        {
            foreach (Match token in Token().Matches(text))
                if (!Allowed(type, token.Groups[1].Value)) throw new NotificationRuleException("UNKNOWN_PLACEHOLDER");
            var remainder = Token().Replace(text, "");
            if (remainder.Contains("{{", StringComparison.Ordinal) || remainder.Contains("}}", StringComparison.Ordinal))
                throw new NotificationRuleException("INVALID_TEMPLATE");
        }
    }

    public static NotificationContent Render(NotificationType type, string subject, string body, IReadOnlyDictionary<string, string> values)
    {
        Validate(type, subject, body);
        string Replace(Match token)
        {
            var key = token.Groups[1].Value;
            if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) throw new NotificationRuleException("MISSING_PLACEHOLDER");
            if (value.Any(char.IsControl)) throw new NotificationRuleException("INVALID_PLACEHOLDER");
            if (key == "appLink" && (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0))
                throw new NotificationRuleException("INVALID_APP_LINK");
            return WebUtility.HtmlEncode(value);
        }
        var rendered = new NotificationContent(Token().Replace(subject, Replace), Token().Replace(body, Replace));
        if (rendered.Subject.Length > 500 || rendered.Body.Length > 20000) throw new NotificationRuleException("CONTENT_TOO_LONG");
        return rendered;
    }

    private static bool Allowed(NotificationType type, string key) => Common.Contains(key, StringComparer.Ordinal)
        || type is NotificationType.LessonScheduled or NotificationType.LessonRescheduled or NotificationType.LessonReminder
        && Lesson.Contains(key, StringComparer.Ordinal);
}
