using Microsoft.AspNetCore.Diagnostics;
using Npgsql;
using Vairas.Notifications.Business;
using Vairas.Notifications.Persistance;

namespace Vairas.Notifications.Middleware;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        // A client that went away is not a server failure; there is nobody to answer.
        if (exception is OperationCanceledException && http.RequestAborted.IsCancellationRequested) return true;
        var (status, code) = exception switch
        {
            BusinessException e => (StatusOf(e), e.Code),
            BadHttpRequestException e => (e.StatusCode, "VALIDATION_FAILED"),
            _ => (500, "INTERNAL_ERROR")
        };
        var failure = exception as PersistenceFailureException;
        logger.Log(status >= 500 ? LogLevel.Error : LogLevel.Warning, "Request failed: {Code}; exception type {ExceptionType}; sqlstate {SqlState}; constraint {Constraint}", code, exception.GetType().Name, failure?.SqlState, failure?.Constraint);
        await http.WriteProblemAsync(status, code, "The notification request could not be completed.", ct);
        return true;
    }

    private static int StatusOf(BusinessException exception) => exception switch
    {
        BadRequestException => StatusCodes.Status400BadRequest,
        UnauthorizedException => StatusCodes.Status401Unauthorized,
        ForbiddenException => StatusCodes.Status403Forbidden,
        NotFoundException => StatusCodes.Status404NotFound,
        ConflictException => StatusCodes.Status409Conflict,
        UnprocessableEntityException => StatusCodes.Status422UnprocessableEntity,
        ServiceUnavailableException => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError
    };
}
