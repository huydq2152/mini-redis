using MyRedis.Abstractions.Commands;
using MyRedis.Abstractions.Configuration;

namespace MyRedis.Commands;

/// <summary>
/// CONFIG command handler with subcommand routing.
///
/// Subcommands:
/// - CONFIG GET &lt;parameter&gt; - Get configuration value(s)
/// - CONFIG SET &lt;parameter&gt; &lt;value&gt; - Set configuration value
/// - CONFIG REWRITE - Save current configuration to redis.conf file
/// 
/// - Supports glob patterns: CONFIG GET max*
/// </summary>
public class ConfigCommandHandler(IConfigurationService configService) : BaseCommandHandler
{
    private readonly IConfigurationService _configService =
        configService ?? throw new ArgumentNullException(nameof(configService));

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
            "REWRITE" => HandleRewriteAsync(context, args),
            _ => HandleUnknownSubcommand(context, subcommand)
        };
    }

    /// <summary>
    /// Handles CONFIG GET command.
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
    /// Handles CONFIG SET command.
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
    /// Handles CONFIG REWRITE
    ///
    /// Performance Note:
    /// - Uses async I/O (File.WriteAllLinesAsync) to avoid blocking the event loop
    /// - During I/O wait, event loop can process other client requests
    /// - Future: Consider offloading to BackgroundTaskSystem for zero blocking
    /// </summary>
    private async Task<bool> HandleRewriteAsync(ICommandContext context, IReadOnlyList<string> args)
    {
        // CONFIG REWRITE requires no additional arguments
        if (args.Count != 1)
        {
            WriteError(context, "ERR wrong number of arguments for 'config rewrite' command");
            return true;
        }

        try
        {
            // Write current configuration to redis.conf file asynchronously
            await Services.Configuration.ConfigurationFile.WriteToFileAsync(_configService);
            context.ResponseWriter.WriteString(context.Connection.Writer, "OK");
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
        WriteError(context,
            $"ERR Unknown CONFIG subcommand '{subcommand}'. Try CONFIG GET, CONFIG SET, or CONFIG REWRITE.");
        return Task.FromResult(true);
    }
}