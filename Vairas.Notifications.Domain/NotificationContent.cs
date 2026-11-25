using System.Net;
using System.Text.RegularExpressions;

namespace Vairas.Notifications.Domain;

public sealed record NotificationContent(string Subject, string Body);
