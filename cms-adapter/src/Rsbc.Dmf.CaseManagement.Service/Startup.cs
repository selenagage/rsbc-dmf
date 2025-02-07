﻿using System;
using System.Net.Http;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Exceptions;
using Serilog.Filters;
using Serilog.Sinks.Splunk;

namespace Rsbc.Dmf.CaseManagement.Service
{
    public class Startup
    {
        /// <summary>
        /// Global configuration
        /// </summary>
        private readonly IConfiguration Configuration;

        public Startup(IConfiguration configuration)
        {
            this.Configuration = configuration;
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddHealthChecks().AddCheck("Case Management Service", () => HealthCheckResult.Healthy("OK"), new[] { "ready" });

            if (!string.IsNullOrEmpty(Configuration["JWT_TOKEN_KEY"]))
            {
                // Configure JWT authentication
                services.AddAuthentication(o =>
                {
                    o.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                    o.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                }).AddJwtBearer(o =>
                {
                    o.SaveToken = true;
                    o.RequireHttpsMetadata = false;
                    o.TokenValidationParameters = new TokenValidationParameters
                    {
                        RequireExpirationTime = false,
                        ValidIssuer = Configuration["JWT_VALID_ISSUER"],
                        ValidAudience = Configuration["JWT_VALID_AUDIENCE"],
                        IssuerSigningKey =
                            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Configuration["JWT_TOKEN_KEY"]))
                    };
                });
            }
            else
            {
                services.AddAuthentication();
            }
                
            services.AddAuthorization();
            services.AddGrpc(opts =>
            {
                opts.EnableDetailedErrors = true;
            });
            services.AddGrpcReflection();
            services.AddDistributedMemoryCache();
            services.AddCaseManagement(Configuration);
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            //app.UseHsts();
            //app.UseHttpsRedirection();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapHealthChecks("/hc/ready", new HealthCheckOptions()
                {
                    Predicate = (check) => check.Tags.Contains("ready")
                });

                endpoints.MapHealthChecks("/hc/live", new HealthCheckOptions()
                {
                    Predicate = (_) => false
                });
                endpoints.MapGrpcService<CaseService>();

                endpoints.MapGet("/", async context =>
                {
                    await context.Response.WriteAsync("Communication with gRPC endpoints must be made through a gRPC client");
                });
                if (env.IsDevelopment())
                {
                    endpoints.MapGrpcReflectionService();
                }
            });


            // enable Splunk logger using Serilog
            if (!string.IsNullOrEmpty(Configuration["SPLUNK_COLLECTOR_URL"]) &&
                !string.IsNullOrEmpty(Configuration["SPLUNK_TOKEN"])
            )
            {
                var fields = new CustomFields();
                if (!string.IsNullOrEmpty(Configuration["SPLUNK_CHANNEL"]))
                    fields.CustomFieldList.Add(new CustomField("channel", Configuration["SPLUNK_CHANNEL"]));

                // Fix for bad SSL issues 


                Log.Logger = new LoggerConfiguration()
                    .Enrich.FromLogContext()
                    .Enrich.WithExceptionDetails()
                    // ensure that logs do not have an entry for each health check
                    .Filter.ByExcluding(Matching.FromSource("Microsoft.AspNetCore.Routing.EndpointMiddleware"))
                    .WriteTo.Console()
                    .WriteTo.EventCollector(Configuration["SPLUNK_COLLECTOR_URL"],
                        sourceType: "cmsadapter", eventCollectorToken: Configuration["SPLUNK_TOKEN"],
                        restrictedToMinimumLevel: LogEventLevel.Information,
#pragma warning disable CA2000 // Dispose objects before losing scope
                        messageHandler: new HttpClientHandler
                        {
                            ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
                            {
                                return true;
                            }
                        }
#pragma warning restore CA2000 // Dispose objects before losing scope
                    )
                    .CreateLogger();
            }
            else
            {
                Log.Logger = new LoggerConfiguration()
                    .Enrich.FromLogContext()
                    .Enrich.WithExceptionDetails()
                    .WriteTo.Console()
                    .CreateLogger();
            }

            SelfLog.Enable(Console.Error);

            Log.Logger.Information("CMS Adapter Container Starting");
        }
    }
}