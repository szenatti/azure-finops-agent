using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI.Tools;

internal sealed class SessionBoundTool(AIFunction inner, long userId, string sessionId) : DelegatingAIFunction(inner)
{
    internal static async Task VerifySessionIdAsync(string? expected, string actual, Func<Task> dispose)
    {
        if (!string.IsNullOrWhiteSpace(expected) && string.Equals(expected, actual, StringComparison.Ordinal)) return;
        await dispose();
        throw new InvalidOperationException("The SDK returned an unexpected session identity; the session was not registered.");
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        using var activity = new Activity("SessionToolInvocation")
            .SetBaggage("finops.turn.id", $"{userId}:{sessionId}")
            .Start();
        return await base.InvokeCoreAsync(arguments, cancellationToken);
    }

    internal static List<AIFunctionDeclaration> Bind(IEnumerable<AIFunctionDeclaration> tools, long userId, string sessionId) =>
        tools.Select(tool => tool is AIFunction function
            ? (AIFunctionDeclaration)new SessionBoundTool(function, userId, sessionId)
            : tool).ToList();
}