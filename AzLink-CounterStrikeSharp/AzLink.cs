using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace AzLink.CounterStrikeSharp;

public class AzLink : BasePlugin, IPluginConfig<AzLinkConfig>
{
    private const string AzLinkVersion = "1.0.0";

    private HttpClient client = new();

    private DateTime lastFullSent = DateTime.Now;
    private DateTime lastSent = DateTime.Now;

    public override string ModuleName => "AzLink";
    public override string ModuleAuthor => "Azuriom";
    public override string ModuleVersion => AzLinkVersion;
    public override string ModuleDescription => "Link your Azuriom website with an Counter-Strike 2 server.";

    public AzLinkConfig Config { get; set; } = new();

    public void OnConfigParsed(AzLinkConfig config)
    {
        Config = config;

        InitHttpClient();

        if (config.SiteKey == null || config.Url == null)
        {
            Logger.LogWarning("AzLink is not configured yet.");
        }
    }

    public override void Load(bool hotReload)
    {
        AddTimer(60, TryFetch, TimerFlags.REPEAT);
    }

    [ConsoleCommand("azlink_setup", "Setup AzLink")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnSetupCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (commandInfo.ArgCount < 3)
        {
            commandInfo.ReplyToCommand(
                "You must first add this server in your Azuriom admin dashboard, in the 'Servers' section.");
            return;
        }

        Config.Url = commandInfo.GetArg(1);
        Config.SiteKey = commandInfo.GetArg(2);

        InitHttpClient();

        PingWebsite(() =>
        {
            commandInfo.ReplyToCommand("Linked to the website successfully.");
            SaveConfig();
        }, code =>
        {
            commandInfo.ReplyToCommand($"An error occurred, code {code}");
            Config.Url = null;
        });
    }

    [ConsoleCommand("azlink_status", "Check the status of AzLink")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnStatusCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (Config.Url == null)
        {
            commandInfo.ReplyToCommand("AzLink is not configured yet, use the 'setup' subcommand first.");
            return;
        }

        PingWebsite(() => commandInfo.ReplyToCommand("Connected to the website successfully."),
            code => commandInfo.ReplyToCommand($"An error occurred, code {code}"));
    }

    [ConsoleCommand("azlink_fetch", "Fetch data from the website")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnFetchCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (Config.Url == null)
        {
            commandInfo.ReplyToCommand("AzLink is not configured yet, use the 'setup' subcommand first.");
            return;
        }

        RunFetch(res =>
        {
            DispatchCommands(res.Commands);

            commandInfo.ReplyToCommand("Data has been fetched successfully.");
        }, code => commandInfo.ReplyToCommand($"An error occurred, code {code}"), true);
    }

    private void TryFetch()
    {
        var now = DateTime.Now;

        if (Config.Url == null || Config.SiteKey == null)
        {
            return;
        }

        if ((now - lastSent).TotalSeconds < 15)
        {
            return;
        }

        lastSent = now;

        var full = now.Minute % 15 == 0 && (now - lastFullSent).TotalSeconds >= 60;

        if (full)
        {
            lastFullSent = now;
        }

        RunFetch(res => DispatchCommands(res.Commands),
            code => Logger.LogError("Unable to send data to the website (code {code})", code), full);
    }

    private void RunFetch(Action<FetchResponse> callback, Action<int> errorHandler, bool sendFullData)
    {
        //Server.NextFrameAsync(() =>
        //{
        var data = GetServerData(sendFullData);

        FetchAsync(callback, errorHandler, data);
        //});
    }

    private async void FetchAsync<T>(Action<FetchResponse> callback, Action<int> errorHandler, T data)
    {
        try
        {
            var res = await client.PostAsJsonAsync("/api/azlink", data);

            if (!res.IsSuccessStatusCode)
            {
                await Server.NextFrameAsync(() => errorHandler((int)res.StatusCode));
                return;
            }

            var fetchRes = await res.Content.ReadFromJsonAsync<FetchResponse>();

            if (fetchRes == null)
            {
                throw new ApplicationException("Unable to parse the response from the website.");
            }

            await Server.NextFrameAsync(() => callback(fetchRes));
        }
        catch (Exception e)
        {
            Logger.LogError("An error occurred while fetching data from the website: {error}", e.Message);

            Console.WriteLine(e);
        }
    }

    private async void PingWebsite(Action onSuccess, Action<int> errorHandler)
    {
        if (Config.Url == null || Config.SiteKey == null)
        {
            throw new ApplicationException("AzLink is not configured yet.");
        }

        try
        {
            var res = await client.GetAsync("/api/azlink");

            if (!res.IsSuccessStatusCode)
            {
                await Server.NextFrameAsync(() => errorHandler((int)res.StatusCode));

                return;
            }

            await Server.NextFrameAsync(onSuccess);
        }
        catch (Exception e)
        {
            Logger.LogError("An error occurred while pinging the website: {error}", e.Message);

            Console.WriteLine(e);
        }
    }

    private void DispatchCommands(ICollection<PendingCommand> commands)
    {
        if (commands.Count == 0)
        {
            return;
        }

        foreach (var info in commands)
        {
            var player = Utilities.GetPlayerFromSteamId(ulong.Parse(info.UserId));
            var name = player?.PlayerName ?? info.UserName;
            var id = player?.UserId?.ToString() ?? info.UserId;

            foreach (var command in info.Values)
            {
                var cmd = command.Replace("{player}", name)
                    .Replace("{id}", id)
                    .Replace("{steam_id}", info.UserId);

                Logger.LogInformation("Dispatching command to {Name} ({User}): {Command}", name, info.UserId, cmd);

                Server.ExecuteCommand(cmd);
            }
        }

        Logger.LogInformation("Dispatched commands to {Count} players.", commands.Count);
    }

    private Dictionary<string, object> GetServerData(bool includeFullData)
    {
        var online = Utilities.GetPlayers().Select(player => new Dictionary<string, string>
        {
            { "name", player.PlayerName }, { "uid", player.SteamID.ToString() }
        });
        var data = new Dictionary<string, object>
        {
            {
                "platform", new Dictionary<string, string>
                {
                    { "type", "COUNTER_STRIKE_SHARP" },
                    { "name", "CounterStrikeSharp" },
                    { "version", Api.GetVersionString() },
                    { "key", "uid" }
                }
            },
            { "version", ModuleVersion },
            { "players", online.ToArray() },
            { "maxPlayers", Server.MaxPlayers },
            { "full", includeFullData }
        };

        if (includeFullData)
        {
            data.Add("ram", GC.GetTotalMemory(false) / 1024 / 1024);
        }

        return data;
    }

    private void InitHttpClient()
    {
        if (Config.Url == null || Config.SiteKey == null)
        {
            return;
        }

        client = new();
        client.BaseAddress = new Uri(Config.Url);
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("Azuriom-Link-Token", Config.SiteKey);
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.DefaultRequestHeaders.Add("User-Agent", $"AzLink CounterStrikeSource v{AzLinkVersion}");
    }

    private void SaveConfig()
    {
        var baseName = Path.GetFileName(ModuleDirectory);
        var configsPath = Path.Combine(ModuleDirectory, "..", "..", "configs", "plugins");
        var jsonConfigPath = Path.Combine(configsPath, baseName, $"{baseName}.json");
        var json = JsonSerializer.Serialize(Config, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(jsonConfigPath, json);
    }
}

public class AzLinkConfig : BasePluginConfig
{
    public string? Url { get; set; }

    public string? SiteKey { get; set; }
}

internal class FetchResponse
{
    [JsonPropertyName("commands")] public List<PendingCommand> Commands { get; set; } = [];
}

internal class PendingCommand
{
    [JsonPropertyName("uid")] public string UserId { get; set; } = "";

    [JsonPropertyName("name")] public string UserName { get; set; } = "";

    [JsonPropertyName("values")] public List<string> Values { get; set; } = [];
}