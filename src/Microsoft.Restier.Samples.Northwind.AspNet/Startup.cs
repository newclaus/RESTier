using System;
using System.Net;
using System.Web.Http;
using System.Web.Http.ExceptionHandling;
using Microsoft.AspNet.OData.Extensions;
using Microsoft.AspNet.OData.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Owin;
using Microsoft.Restier.EntityFramework;
using Microsoft.Restier.Samples.Northwind.AspNet;
using Microsoft.Restier.Samples.Northwind.AspNet.Controllers;
using Microsoft.Restier.Samples.Northwind.AspNet.Data;
using Owin;

[assembly: OwinStartup(typeof(Startup))]
namespace Microsoft.Restier.Samples.Northwind.AspNet
{
    public class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            var config = new HttpConfiguration();
            var httpServer = new HttpServer(config); // use http server

#if !PROD
            config.IncludeErrorDetailPolicy = IncludeErrorDetailPolicy.Always;
#endif

            config.Filter().Expand().Select().OrderBy().MaxTop(100).Count().SetTimeZoneInfo(TimeZoneInfo.Utc);

            config.UseRestier<NorthwindApi>((services) =>
            {
                // This delegate is executed after OData is added to the container.
                // Add you replacement services here.
                services.AddEF6ProviderServices<NorthwindEntities>();

                services.AddSingleton(new ODataValidationSettings
                {
                    MaxTop = 5,
                    MaxAnyAllExpressionDepth = 3,
                    MaxExpansionDepth = 3,
                });
            });

            config.MapRestier<NorthwindApi>("ApiV1", "", httpServer); // use new overload with http server

            app.UseWebApi(httpServer); // configure webApi using httpServer
        }
    }
}