using MyRedis.Abstractions.Commands;
using MyRedis.Abstractions.Configuration;

namespace MyRedis.Commands;

/// <summary>
/// CONFIG command handler with subcommand routing.
///
/// Subcommands:
/// - CONFIG GET &lt;parameter&gt; - Get configuration value(s)
/// - CONFIG SET &lt;parameter&gt; &lt;value&gt; - Set configuration value
///
/// Redis Protocol:
/// - CONFIG GET returns array: [param1, value1, param2, value2, ...]
/// - CONFIG SET returns "OK" on success, error otherwise
/// - Supports glob patterns: CONFIG GET max*
///
/// Examples:
/// &gt; CONFIG GET maxclients
/// 1) "maxclients"
/// 2) "10000"
///
/// &gt; CONFIG GET *
/// 1) "port"
/// 2) "6379"
/// 3) "timeout"
/// 4) "300"
/// ...
///
/// &gt; CONFIG SET timeout 600
/// OK
/// </summary>
public class ConfigCommandHandler : BaseCommandHandler
{
    private readonly IConfigurationService _configService;

    public ConfigCommandHandler(IConfigurationService configService)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
    }

    public override string CommandName => "CONFIG";

    public override Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args)
    {
        // CONFIG requires at least one argument (the subcommand)
        if (args.Count == 0)
        {
            WriteError(context, "ERR wrong number of arguments for 'config' command");
            return Task.FromResult(true);
        }

        var subcommand = args[0].ToUpper();

        return subcommand switch
        {
            "GET" => HandleGetAsync(context, args),
            "SET" => HandleSetAsync(context, args),
            _ => HandleUnknownSubcommand(context, subcommand)
        };
    }

    /// <summary>
    /// Handles CONFIG GET &lt;parameter&gt;
    /// </summary>
    private Task<bool> HandleGetAsync(ICommandContext context, IReadOnlyList<string> args)
    {
        // CONFIG GET requires exactly one parameter
        if (args.Count != 2)
        {
            WriteError(context, "ERR wrong number of arguments for 'config get' command");
            return Task.FromResult(true);
        }

        var pattern = args[1];

        try
        {
            // Get matching parameters
            var matches = _configService.GetMatching(pattern);

            // Build response array: [p1, v1, p2, v2, ...]
            var responseSize = matches.Count * 2;
            context.ResponseWriter.WriteArrayHeader(context.Connection.Writer, responseSize);

            foreach (var (name, value) in matches)
            {
                context.ResponseWriter.WriteString(context.Connection.Writer, name);
                context.ResponseWriter.WriteString(context.Connection.Writer, value);
            }

            return Task.FromResult(true);
        }
        catch (Exception e)
        {
            WriteError(context, $"ERR {e.Message}");
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Handles CONFIG SET &lt;parameter&gt; &lt;value&gt;
    /// </summary>
    private async Task<bool> HandleSetAsync(ICommandContext context, IReadOnlyList<string> args)
    {
        // CONFIG SET requires exactly two parameters (name and value)
        if (args.Count != 3)
        {
            WriteError(context, "ERR wrong number of arguments for 'config set' command");
            return true;
        }

        var name = args[1];
        var value = args[2];

        try
        {
            // Attempt to set the parameter
            var result = await _configService.SetAsync(name, value);

            if (result.Success)
            {
                // Check if hot-reloadable
                var param = _configService.GetParameterMetadata(name);
                if (param != null && !param.IsHotReloadable)
                {
                    // Warning: requires restart
                    context.ResponseWriter.WriteString(context.Connection.Writer,
                        "OK (restart required for this change to take effect)");
                }
                else
                {
                    context.ResponseWriter.WriteString(context.Connection.Writer, "OK");
                }
            }
            else
            {
                WriteError(context, result.ErrorMessage!);
            }

            return true;
        }
        catch (Exception e)
        {
            WriteError(context, $"ERR {e.Message}");
            return true;
        }
    }

    /// <summary>
    /// Handles unknown subcommands.
    /// </summary>
    private Task<bool> HandleUnknownSubcommand(ICommandContext context, string subcommand)
    {
        WriteError(context, $"ERR Unknown CONFIG subcommand '{subcommand}'. Try CONFIG GET or CONFIG SET.");
        return Task.FromResult(true);
    }
}
