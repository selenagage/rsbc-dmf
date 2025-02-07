﻿using IdentityServer4;
using IdentityServer4.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.IO;
using System.Text.Json;

namespace OAuthServer
{
    public class Startup
    {
        private const string HealthCheckReadyTag = "ready";
        private readonly IWebHostEnvironment environment;
        private readonly IConfiguration configuration;

        public Startup(IWebHostEnvironment environment, IConfiguration configuration)
        {
            this.environment = environment;
            this.configuration = configuration;
        }

        public void ConfigureServices(IServiceCollection services)
        {
            var dpBuilder = services.AddDataProtection();
            var keyRingPath = configuration.GetValue<string>("KEY_RING_PATH");
            if (!string.IsNullOrWhiteSpace(keyRingPath))
            {
                //configure data protection folder for key sharing
                dpBuilder.PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));
            }

            services.AddControllersWithViews();

            var connectionString = configuration.GetConnectionString("DefaultConnection");
            var config = JsonSerializer.Deserialize<Config>(File.ReadAllText("./Data/config.json"));
            var builder = services
                .AddIdentityServer(options =>
                {
                    options.Events.RaiseErrorEvents = true;
                    options.Events.RaiseInformationEvents = true;
                    options.Events.RaiseFailureEvents = true;
                    options.Events.RaiseSuccessEvents = true;

                    options.UserInteraction.LoginUrl = "~/login";
                    options.UserInteraction.LogoutUrl = "~/logout";

                    if (!string.IsNullOrEmpty(configuration["ISSUER_URI"])) options.IssuerUri = configuration["ISSUER_URI"];
                })

                .AddOperationalStore(options =>
                {
                    options.ConfigureDbContext = builder => builder.UseSqlite(connectionString, sql => sql.MigrationsAssembly(typeof(Startup).Assembly.FullName));
                    options.EnableTokenCleanup = true;
                })
                .AddInMemoryApiScopes(config.ApiScopes)
                .AddInMemoryClients(config.Clients)
                .AddInMemoryIdentityResources(config.IdentityResources)
                .AddInMemoryApiResources(config.ApiResources)
                .AddInMemoryCaching()
                ;

            //store the oidc key in the key ring persistent volume
            var keyPath = Path.Combine(new DirectoryInfo(keyRingPath ?? "./Data").FullName, "oidc_key.jwk");

            //add key as signing key
            builder.AddDeveloperSigningCredential(filename: keyPath);

            //add key as encryption key to oidc jwks endpoint that is used by BCSC to encrypt tokens
            var encryptionKey = Microsoft.IdentityModel.Tokens.JsonWebKey.Create(File.ReadAllText(keyPath));
            encryptionKey.Use = "enc";
            builder.AddValidationKey(new SecurityKeyInfo { Key = encryptionKey });

            services.AddOidcStateDataFormatterCache();
            services.AddDistributedMemoryCache();
            services.AddResponseCompression();
            services.AddDatabaseDeveloperPageExceptionFilter();

            services.AddAuthentication()
                .AddOpenIdConnect("bcsc", options =>
                {
                    configuration.GetSection("identityproviders:bcsc").Bind(options);
                    options.SaveTokens = true;
                    options.GetClaimsFromUserInfoEndpoint = false;
                    options.UseTokenLifetime = true;
                    options.ResponseType = OpenIdConnectResponseType.Code;
                    options.SignInScheme = IdentityServerConstants.ExternalCookieAuthenticationScheme;
                    options.SignOutScheme = IdentityServerConstants.ExternalCookieAuthenticationScheme;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        NameClaimType = "name",
                        RoleClaimType = "role"
                    };

                    //add required scopes
                    options.Scope.Add("address");
                    options.Scope.Add("email");

                    //set the tokens decrypting key
                    options.TokenValidationParameters.TokenDecryptionKey = encryptionKey;

                    //options.Events = new OpenIdConnectEvents
                    //{
                    //    OnTokenResponseReceived = async ctx =>
                    //    {
                    //        await Task.CompletedTask;
                    //    },
                    //    OnTokenValidated = async ctx =>
                    //    {
                    //        await Task.CompletedTask;
                    //    },
                    //    OnRemoteFailure = async ctx =>
                    //    {
                    //        await Task.CompletedTask;
                    //    },
                    //    OnAuthenticationFailed = async ctx =>
                    //    {
                    //        await Task.CompletedTask;
                    //    },
                    //    OnUserInformationReceived = async ctx =>
                    //    {
                    //        await Task.CompletedTask;
                    //    },
                    //    OnTicketReceived = async ctx =>
                    //    {
                    //        await Task.CompletedTask;
                    //    }
                    //};
                });

            services.AddHealthChecks().AddCheck("OAuth Server ", () => HealthCheckResult.Healthy("OK"), new[] { HealthCheckReadyTag });
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.All;
                options.KnownNetworks.Clear();
                options.KnownProxies.Clear();
            });
        }

        public void Configure(IApplicationBuilder app)
        {
            app.UseForwardedHeaders();
            if (environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                IdentityModelEventSource.ShowPII = true;
            }

            app.UseXContentTypeOptions();
            app.UseReferrerPolicy(opts => opts.NoReferrer());
            app.UseXXssProtection(opts => opts.EnabledWithBlockMode());

            //app.UseXfo(xfo => xfo.SameOrigin());
            app.UseXRobotsTag(options => options.NoIndex().NoFollow());

            app.UseCsp(opts => opts
                .DefaultSources(opts => opts.Self().CustomSources("https://*.gov.bc.ca", "https://*.bcgov"))
                .BlockAllMixedContent());

            app.UseResponseCompression();
            app.UseCookiePolicy();

            app.UsePathBase(configuration["BASE_PATH"] ?? "");
            app.UseRouting();
            app.UseIdentityServer();
            app.UseAuthentication();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapDefaultControllerRoute();
                endpoints.MapHealthChecks("/hc/ready", new HealthCheckOptions()
                {
                    Predicate = (check) => check.Tags.Contains(HealthCheckReadyTag)
                });

                endpoints.MapHealthChecks("/hc/live", new HealthCheckOptions()
                {
                    Predicate = (_) => false
                });
            });
        }
    }
}