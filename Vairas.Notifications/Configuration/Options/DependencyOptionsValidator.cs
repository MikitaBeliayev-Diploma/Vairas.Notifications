using Microsoft.Extensions.Options;

namespace Vairas.Notifications.Configuration.Options;

public sealed class DependencyOptionsValidator : IValidateOptions<DependencyOptions>
{
    public ValidateOptionsResult Validate(string? name, DependencyOptions options) =>
        new[] { options.AuthBaseUrl, options.UserManagementBaseUrl, options.SchoolBaseUrl, options.SchedulingBaseUrl, options.LearningBaseUrl }
            .All(OriginValidation.IsExplicitHttpsOrigin)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Dependencies require absolute HTTPS origins without credentials.");
}
