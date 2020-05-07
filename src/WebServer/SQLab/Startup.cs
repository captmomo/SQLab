using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SpaServices.Webpack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqCommon;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.Logging;

namespace SQLab
{
  
    public class Startup
    {
        public Startup(Microsoft.Extensions.Configuration.IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public Microsoft.Extensions.Configuration.IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddMvc();
            //Transient objects are always different; a new instance is provided to every controller and every service.
            //Scoped objects are the same within a request, but different across different requests
            //Singleton objects are the same for every object and every request(regardless of whether an instance is provided in ConfigureServices)
            services.AddSingleton(_ => Utils.Configuration);      // this is the proper DependenciInjection (DI) way of pushing it as a service to Controllers. So you don't have to manage the creation or disposal of instances.
            services.AddSingleton(_ => Program.g_webAppGlobals);
    
            if (!String.IsNullOrEmpty(Utils.Configuration["GoogleClientId"]) && !String.IsNullOrEmpty(Utils.Configuration["GoogleClientSecret"]))
            {
                // The reason you have BOTH google and cookies Auth is because you're using google for identity information but using cookies for storage of the identity for only asking Google once.
                //So AddIdentity() is not required, but Cookies Yes.
                services.AddAuthentication(options =>
                {
                    // If you don't want the cookie to be automatically authenticated and assigned to HttpContext.User, 
                    // remove the CookieAuthenticationDefaults.AuthenticationScheme parameter passed to AddAuthentication.
                    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;  // For anything else (sign in, sign out, authenticate, forbid), use the cookies scheme
                    options.DefaultChallengeScheme = GoogleDefaults.AuthenticationScheme;   // For challenges, use the google scheme. If not, "InvalidOperationException: No authenticationScheme was specified"

                    options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                    options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                })
                .AddCookie(o => {  // CookieAuth will be the default from the two, GoogleAuth is used only for Challenge
                    o.LoginPath = "/account/login";
                    o.LogoutPath = "/account/logout";

                    // Controls how much time the authentication ticket stored in the cookie will remain valid
                    // This is separate from the value of Microsoft.AspNetCore.Http.CookieOptions.Expires, which specifies how long the browser will keep the cookie. We will set that in OnTicketReceived()
                    o.ExpireTimeSpan = TimeSpan.FromDays(25);
                })
                .AddGoogle("Google", options =>
                {
                    options.ClientId = Utils.Configuration["GoogleClientId"];
                    options.ClientSecret = Utils.Configuration["GoogleClientSecret"];
                    options.Events = new OAuthEvents
                    {
                        OnCreatingTicket = context =>
                        {
                            string email = context.User.Value<Newtonsoft.Json.Linq.JArray>("emails")[0]["value"].ToString();
                            Utils.Logger.Debug($"[Authorize] attribute forced Google auth. Email:'{email ?? "null"}', RedirectUri: '{context.Properties.RedirectUri ?? "null"}'");

                            if (!Utils.IsAuthorizedGoogleUsers(Utils.Configuration, email))
                                throw new Exception($"Google Authorization Is Required. Your Google account: '{ email }' is not accepted. Logout this Google user and login with another one.");

                            //string domain = context.User.Value<string>("domain");
                            //if (domain != "jerriepelser.com")
                            //    throw new GoogleAuthenticationException("You must sign in with a jerriepelser.com email address");

                            return Task.CompletedTask;
                        },
                        OnTicketReceived = context =>
                        {
                            // if this is not set, then the cookie in the browser expires, even though the validation-info in the cookie is still valid. By default, cookies expire: "When the browsing session ends" Expires: 'session'
                            // https://www.jerriepelser.com/blog/managing-session-lifetime-aspnet-core-oauth-providers/
                            context.Properties.IsPersistent = true;
                            context.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(25);

                            return Task.FromResult(0);
                        }
                    };
                });
            }
            else
            {
                Console.WriteLine("A_G_CId and A_G_CSe from Config has NOT been found. Cannot initialize GoogelAuthentication.");
                Utils.Logger.Warn("A_G_CId and A_G_CSe from Config has NOT been found. Cannot initialize GoogelAuthentication.");
            }

            services.AddLogging(loggingBuilder =>
            {
                loggingBuilder.AddConfiguration(Configuration.GetSection("LoggingToConsole"));
                loggingBuilder.AddConsole();
                loggingBuilder.AddDebug();

                // set nLog here if NLog works properly
                loggingBuilder.AddProvider(new SQLabAspLoggerProvider());
            });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory)
        {
            //loggerFactory.MinimumLevel = LogLevel.Information;
            // root min level: you have to set this the most detailed, because if not ASPlog will not pass it to the NLogExtension
            //loggerFactory.MinimumLevel = Microsoft.Extensions.Logging.LogLevel.Debug;

            // A. ASP.NET5 LogLevels can come from appsettings.json or set here programmatically. But Configuration.GetSection("") returns text from the file, 
            // and not the file itself, so ConsoleLogger is not able to reload config when appsettings.json file is changed. 
            // For that you need a FileSystem watcher manually, which is not OK on Linux systems I guess or not on DNXCore
            // Therefore logging level cannot be changed by modifying that file (at least, not without extra FileSystemWatcher programming)
            // B. On the other hand Nlog is based on DNX, not DNXCore, and implements FileSystemWatcher properly, and I tested it and 
            // when the app.nlog file is changed by Notepad nLog under Asp.NET notices the LogLevelChange.
            
            //var x = Configuration.GetSection("LoggingToConsole");   // it is null
            //loggerFactory.AddConsole(Configuration.GetSection("LoggingToConsole"));
            ////loggerFactory.AddConsole(LogLevel.Debug);     // write to the Console  (if available) window as Colorful multiline (in Kestrel??) . MinLevel can be specified. by default it is LogLevel.Information
            //loggerFactory.AddDebug(Microsoft.Extensions.Logging.LogLevel.Trace);       // write to the Debug output window (in VS). MinLevel can be specified. by default it is LogLevel.Information
                        
            // set nLog here if NLog works properly
            // loggerFactory.AddProvider(new SQLabAspLoggerProvider());
            
            string envLogMsg = $"ASP env.EnvironmentName(machine-wide ASPNETCORE_ENVIRONMENT EnvVar or C# .UseEnvironment()):'{env.EnvironmentName}'";
            Console.WriteLine(envLogMsg);
            Utils.Logger.Info(envLogMsg);

            var aspLogLevel = Configuration.GetSection("Logging:LogLevel:Microsoft");
            string aspLogLevelStr = (aspLogLevel != null) ? aspLogLevel.Value : "NotAvailable";
            string logLevelMsg = $"ASP logLevel as appsettings.json or appsettings.Development.json:'Logging:LogLevel:Microsoft':'{aspLogLevelStr}'";
            Console.WriteLine(logLevelMsg);
            Utils.Logger.Info(logLevelMsg);

            if (env.IsDevelopment())
            {
                //Now, assuming you're running in development mode, any requests for files under /dist will be intercepted and served using Webpack dev middleware.
                //This is for development time only, not for production use (hence the env.IsDevelopment() check in the code above). 
                app.UseDeveloperExceptionPage();     // ExceptionHandlers will swallow the Exceptions. It will not be rolled further.
                app.UseWebpackDevMiddleware(new WebpackDevMiddlewareOptions
                {
                    HotModuleReplacement = true  //This watches for any changes you make to source files on disk (e.g., .ts/.html/.sass/etc. files), and automatically rebuilds them and pushes the result into your browser window, without even needing to reload the page.
                });
            }
            else
            {
                app.UseExceptionHandler("/Home/Error");     // ExceptionHandlers will swallow the Exceptions. It will not be rolled further.
            }

            app.UseMiddleware<SqFirewallMiddleware>();  // For this to catch Exceptions, it should come after UseExceptionHadlers(), because those will swallow exceptions and generates nice ErrPage.

            app.UseStaticFiles();   // Call UseWebpackDevMiddleware before UseStaticFiles 

            app.UseAuthentication();    // StaticFiles are served Before the user is authenticed. This is fast, but httpContext?.User?.Claims is null in this case.

            app.UseMvc(routes =>
            {
                routes.MapRoute(
                    name: "default",
                    template: "{controller=Home}/{action=Index}/{id?}");

                // Since UseStaticFiles goes first, any requests that actually match physical files under wwwroot will be handled by serving that static file.
                //Since the default server - side MVC route goes next, any requests that match existing controller / action pairs will be handled by invoking that action.
                //Then, since MapSpaFallbackRoute is last, any other requests that don't appear to be for static files will be served by invoking the Index action on HomeController. 
                //This action's view should serve your client-side application code, allowing the client-side routing system to handle whatever URL has been requested.
                //Any requests that do appear to be for static files (i.e., those that end with filename extensions), will not be handled by MapSpaFallbackRoute, and so will end up as 404s.
                routes.MapSpaFallbackRoute(
                    name: "spa-fallback",
                    defaults: new { controller = "Home", action = "Index" });
            });

        }
    }



}
