using AiShop.Application.Sessions;
using AiShop.Application.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace AiShop.Application;

public static class DependencyInjection
{
    /// <summary>Registers the orchestration services. Infrastructure supplies everything they depend on.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<SessionManager>();
        services.AddSingleton<ISessionManager>(sp => sp.GetRequiredService<SessionManager>());
        services.AddSingleton<IWorkflowEngine, WorkflowEngine>();
        services.AddSingleton<IQuickChatService, QuickChatService>();
        return services;
    }
}
