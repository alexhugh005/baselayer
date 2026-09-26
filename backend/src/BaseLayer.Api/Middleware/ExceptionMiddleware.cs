using BaseLayer.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
namespace BaseLayer.Api.Middleware;

public sealed class ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            var status = exception switch
            {
                ArgumentException => 400,
                KeyNotFoundException => 404,
                UnauthorizedAccessException => 401,
                BatteryProviderException or HttpRequestException or TaskCanceledException => 502,
                _ => 500
            };
            if (status == 500)
                logger.LogError(exception, "Request failed");
            var title = status switch
            {
                500 => "An unexpected error occurred.",
                502 when exception is BatteryProviderException => "Battery telemetry is unavailable. Try again later.",
                502 => "Home Assistant could not complete the request. Check its address and connection, then try connecting again.",
                _ => exception.Message
            };
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = title });
        }
    }
}
