using System.Net.Http.Headers;
using GlitchPilot.Core.Configuration;
using GlitchPilot.Core.Pipelines;
using GlitchPilot.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GlitchPilot.Core;

public static class Registration
{
    public static IServiceCollection AddGlitchPilot(this IServiceCollection services)
    {
        services.AddHttpClient<IGlitchTipClient, GlitchTipClient>((_, http) =>
        {
            var baseUrl = EnvironmentConfig.GlitchTipBaseUrl.TrimEnd('/') + "/api/0/";
            http.BaseAddress = new Uri(baseUrl);
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", EnvironmentConfig.GlitchTipApiToken);
            http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        });

        services.AddSingleton<IErrorClassifier, ErrorClassifier>();
        services.AddSingleton<IIssueFiler, IssueFiler>();
        services.AddSingleton<ISmtpMailer, SmtpMailer>();
        services.AddTransient<TriagePipeline>();
        services.AddTransient<VerifyPipeline>();
        services.AddTransient<ProbePipeline>();

        return services;
    }
}
