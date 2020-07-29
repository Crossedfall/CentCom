﻿using CentCom.Common.Configuration;
using CentCom.Common.Data;
using CentCom.Server.BanSources;
using CentCom.Server.Data;
using CentCom.Server.Quartz;
using CentCom.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;
using Quartz.Impl;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CentCom.Server
{
    class Program
    {
        private static IScheduler _scheduler;
        private static IServiceProvider _serviceProvider;
        private static IConfiguration _configuration;

        static async Task Main(string[] args)
        {
            BuildConfiguration(args);

            // Get a scheduler factory and scheduler
            StdSchedulerFactory factory = new StdSchedulerFactory();
            _scheduler = await factory.GetScheduler();

            // Build services provider and register it with the job factory
            RegisterServices();
            _scheduler.JobFactory = new JobFactory(_serviceProvider);

            // Add updater job
            IJobDetail job = JobBuilder.Create<DatabaseUpdater>()
                .WithIdentity("updater")
                .Build();

            ITrigger trigger = TriggerBuilder.Create()
                .WithIdentity("updaterTrigger")
                .StartNow()
                .Build();

            await _scheduler.ScheduleJob(job, trigger);

            // Start scheduler
            await _scheduler.Start();

            // Run infinitely
            await Task.Delay(-1);
            DisposeServices();
        }

        public static void BuildConfiguration(string[] args)
        {
            _configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
                .AddCommandLine(args)
                .Build();
        }

        public static async Task RegisterJobs()
        {
            var parsers = AppDomain.CurrentDomain.GetAssemblies().Aggregate(new List<Type>(), (curr, next) =>
            {
                curr.AddRange(next.GetTypes().Where(x => x.IsSubclassOf(typeof(BanParser))));
                return curr;
            });

            foreach (var p in parsers)
            {
                IJobDetail regularJob = JobBuilder.Create(p)
                    .WithIdentity(p.Name, "parsers")
                    .Build();

                ITrigger regularTrigger = TriggerBuilder.Create()
                    .WithIdentity($"{p.Name}Trigger", "parsers")
                    .UsingJobData("completeRefresh", false)
                    .WithCronSchedule("0 5-25/5,35-55/5 * * * ?") // Every 5 minutes except at the half hours
                    .StartNow()
                    .Build();

                ITrigger fullTrigger = TriggerBuilder.Create()
                    .WithIdentity($"{p.Name}FullRefreshTrigger", "parsersFullRefresh")
                    .UsingJobData("completeRefresh", true)
                    .WithCronSchedule("0 0,30 * * * ?") // Every half hour
                    .StartNow()
                    .Build();

                await _scheduler.ScheduleJob(regularJob, new[] { regularTrigger, fullTrigger }, false);
            }
        }

        public static void RegisterServices()
        {
            var services = new ServiceCollection();

            // Add logging
            services.AddLogging(x => x.AddConsole());

            // Add scheduler
            services.AddSingleton(_scheduler);

            // Add config
            services.AddSingleton(_configuration);

            // Get DB configuration
            var dbConfig = new DbConfig();
            _configuration.Bind("dbConfig", dbConfig);

            // Add appropriate DB context
            if (dbConfig == null)
            {
                throw new Exception("Failed to read DB configuration, please ensure you provide one in appsettings.json");
            }
            switch (dbConfig.DbType)
            {
                case DbType.Postgres:
                    services.AddDbContext<DatabaseContext, NpgsqlDbContext>();
                    break;
                case DbType.MariaDB:
                case DbType.MySql:
                    services.AddDbContext<DatabaseContext, MySqlDbContext>();
                    break;
            }

            // Add ban services as singletons
            services.AddSingleton<BeeBanService>();
            services.AddSingleton<VgBanService>();
            services.AddSingleton<YogBanService>();

            // Add ban parsers
            var parsers = AppDomain.CurrentDomain.GetAssemblies().Aggregate(new List<Type>(), (curr, next) =>
            {
                curr.AddRange(next.GetTypes().Where(x => x.IsSubclassOf(typeof(BanParser))));
                return curr;
            });

            foreach (var p in parsers)
            {
                services.AddTransient(p);
            }

            // Add jobs
            services.AddTransient<DatabaseUpdater>();

            _serviceProvider = services.BuildServiceProvider(true);
        }

        public static void DisposeServices()
        {
            if (_serviceProvider == null)
            {
                return;
            }

            if (_serviceProvider is IDisposable)
            {
                ((IDisposable)_serviceProvider).Dispose();
            }
        }
    }
}