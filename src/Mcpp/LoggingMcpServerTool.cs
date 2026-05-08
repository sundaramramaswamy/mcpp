using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Mcpp.Indexing;

namespace Mcpp;

/// <summary>
/// Wraps every MCP tool with invocation logging via the SDK's DelegatingMcpServerTool.
/// Logs tool name + args before invocation and exceptions if they occur.
/// </summary>
internal sealed class LoggingMcpServerTool(McpServerTool innerTool) : DelegatingMcpServerTool(innerTool)
{
    /// <summary>
    /// Replaces each registered McpServerTool descriptor with a logging wrapper.
    /// Call after WithToolsFromAssembly() discovery.
    /// </summary>
    public static void WrapAll(IServiceCollection services)
    {
        var descriptors = services
            .Select((sd, i) => (sd, i))
            .Where(x => x.sd.ServiceType == typeof(McpServerTool))
            .ToList();

        foreach (var (sd, idx) in descriptors)
        {
            var original = sd;
            services[idx] = ServiceDescriptor.Singleton<McpServerTool>(sp =>
            {
                McpServerTool inner = original.ImplementationInstance as McpServerTool
                    ?? (original.ImplementationFactory?.Invoke(sp) as McpServerTool)!;
                return new LoggingMcpServerTool(inner);
            });
        }
    }

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var toolName = ProtocolTool.Name;
        var args = request.Params?.Arguments;
        var argsSummary = args != null
            ? string.Join(", ", args.Select(kv => $"{kv.Key}={kv.Value}"))
            : "";

        McpLogger.Log("Tool", $"{toolName}({argsSummary})");

        try
        {
            var sw = Stopwatch.StartNew();
            var result = await base.InvokeAsync(request, cancellationToken);
            sw.Stop();
            McpLogger.Log("Tool", $"{toolName} completed in {sw.ElapsedMilliseconds}ms");
            return result;
        }
        catch (Exception ex)
        {
            McpLogger.Log("Tool", $"{toolName} FAILED: {ex}", "ERROR");
            throw;
        }
    }
}
