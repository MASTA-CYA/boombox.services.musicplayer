
using Microsoft.AspNetCore.SignalR;
using MusicPlayer.Common;
using MusicPlayer.FileManagement;
using MusicServer.Hubs;
using MusicServer.Startup;
using Serilog;
using Serilog.Events;

namespace MusicServer
{
    public class Program
    {
        [STAThread]
        public static async Task Main(string[] args)
        {
            // Configured before WebApplication.CreateBuilder so startup failures (e.g. the AddressInUseException
            // seen in Logs/error.log) get captured too, not just failures after the host is already up. Writes
            // straight to disk instead of relying on NSSM's stdout/stderr redirection (Logs/logs.log,
            // Logs/error.log), which was silently losing most application-level logging — see KNOWN_ISSUES.md.
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .Enrich.FromLogContext()
                .WriteTo.Console()
                .WriteTo.File(
                    Path.Combine("Logs", "app-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: true)
                // Replaces LoggingController's forwarding of Warning/Error entries to ServerHub — see
                // SignalRErrorSink.cs. SignalRErrorSink.Publish isn't wired up until after app.Build() below, since
                // it needs IHubContext<ServerHub>, which doesn't exist yet at this point.
                .WriteTo.Sink(new SignalRErrorSink(LogEventLevel.Warning))
                .CreateLogger();

            try
            {
                Log.Information("MusicServer starting up");

                // Catches whatever would otherwise crash the process silently — see the repeating
                // DynamicPlaylistSampleProvider NullReferenceException in Logs/error.log — and gets one last
                // write to the real log file in before NSSM restarts the service.
                AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
                    Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception (IsTerminating: {IsTerminating})", e.IsTerminating);

                TaskScheduler.UnobservedTaskException += (sender, e) =>
                {
                    Log.Error(e.Exception, "Unobserved task exception");
                    e.SetObserved();
                };

                var builder = WebApplication.CreateBuilder(args);

                // Replaces the default console-only logging provider. Every ILogger<T> injected throughout the app
                // (BroadcastController, LibraryHub, etc.) flows through here automatically.
                builder.Host.UseSerilog();

                builder.Services.AddStackExchangeRedisCache(options =>
                {
                    options.Configuration = builder.Configuration.GetConnectionString("Redis");
                    options.InstanceName = "MusicServer_";
                });
                builder.Services.AddSignalR(options =>
                {
                    options.MaximumReceiveMessageSize = null; // DOS Risk
                }).AddNewtonsoftJsonProtocol(options =>
                {
                    options.PayloadSerializerSettings.ContractResolver =
                        new Newtonsoft.Json.Serialization.DefaultContractResolver();
                });
                builder.Services.AddCors(options => options.AddPolicy("CorsPolicy", builder =>
                builder.AllowAnyMethod().AllowAnyHeader().AllowCredentials().WithOrigins("http://localhost:9878")));
                builder.Services.AddOutputCache();
                builder.Services.AddControllers();
                builder.Services.AddEndpointsApiExplorer();
                builder.Services.AddSwaggerGen();

                var app = builder.Build();

                // Must run before InitializeAsync()/MappingUpdateBroadcast.Initialize() below or anything else
                // that could touch a MusicPlayer singleton (LibraryManager.Instance, FileManager.Instance, etc.) —
                // each of those reads AppLogger.CreateLogger<T>() once, at type-initialization time, so whatever
                // factory AppLogger holds at that moment is what they're stuck with for the process's lifetime.
                AppLogger.Initialize(app.Services.GetRequiredService<ILoggerFactory>());

                var serverHubContext = app.Services.GetRequiredService<IHubContext<ServerHub>>();
                SignalRErrorSink.Publish = message => serverHubContext.Clients.All.SendAsync("ReceiveServerHubError", message);

                //Start up operations
                await InitializeAsync();
                MappingUpdateBroadcast.Initialize(app);

                // Configure the HTTP request pipeline.
                if (app.Environment.IsDevelopment())
                {
                    app.UseSwagger();
                    app.UseSwaggerUI();
                }

                //app.UseHttpsRedirection();
                app.UseAuthorization();
                app.UseOutputCache();
                app.MapControllers();
                app.MapHub<LibraryHub>("/LibraryHub");
                app.MapHub<PlayerHub>("/PlayerHub");
                app.MapHub<PlaylistHub>("/PlaylistHub");
                app.MapHub<ServerHub>("/ServerHub");
                app.MapHub<AutoScrollHub>("/AutoScrollHub");

                app.Run();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "MusicServer terminated unexpectedly");
                throw;
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        private static async Task InitializeAsync()
        {
            await Task.CompletedTask;
            FileManager.Instance.ClearDirectory(Constants.RESAMPLED_PROVIDERS_DIRECTORY);
        }
    }
}
