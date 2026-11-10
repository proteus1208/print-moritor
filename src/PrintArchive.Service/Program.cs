using PrintArchive.Service;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "PrintArchive";
});

builder.Logging.AddFilter<EventLogLoggerProvider>(
    (category, level) => level >= LogLevel.Information);

builder.Services.AddHostedService<PrintMonitorWorker>();

await builder.Build().RunAsync();
