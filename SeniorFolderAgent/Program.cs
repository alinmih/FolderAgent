using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using System.IO;
using Serilog.Events;
using Microsoft.Extensions.Configuration;

namespace SeniorFolderAgent
{
    public class Program
    {
        public static void Main(string[] args)
        {

            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(baseDir)
                    .AddJsonFile("appsettings.json")
                    .Build();
                Log.Logger = new LoggerConfiguration()
                    .ReadFrom.Configuration(configuration)
                    .CreateLogger();

                //Log.Logger = new LoggerConfiguration()
                //.MinimumLevel.Debug()
                //.MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                //.Enrich.FromLogContext()
                //.WriteTo.Console()
                //.WriteTo.File(@$"{Directory.GetCurrentDirectory()}\Logs\LogFile.txt")
                //.CreateLogger();

                Log.Information("Starting up the service");

                CreateHostBuilder(args).Build().Run();

                return;
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "There was a problem starting the service");

                return;
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddHostedService<Worker>();
                }).UseWindowsService().UseSerilog();
    }
}
