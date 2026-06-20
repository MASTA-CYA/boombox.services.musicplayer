
using MusicPlayer.Common;
using MusicPlayer.FileManagment;
using MusicServer.Hubs;

namespace MusicServer
{
    public class Program
    {
        [STAThread]
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Logging.AddConsole();

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

            //Start up operations
            await InitializeAsync();

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

        private static async Task InitializeAsync()
        {
            await Task.CompletedTask;
            FileManager.Instance.ClearDirectory(Constants.RESAMPLED_PROVIDERS_DIRECTORY);
        }
    }
}
