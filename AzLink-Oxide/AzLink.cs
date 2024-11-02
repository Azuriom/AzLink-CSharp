using Newtonsoft.Json;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Oxide.Plugins;

[Info("AzLink", "Azuriom", AzLinkVersion)]
[Description("Link your Azuriom website with an Oxide server.")]
class AzLink : CovalencePlugin
{
    private const string AzLinkVersion = "1.0.0";

    private Dictionary<string, UserInfo> usersBySteamId = new();

    private DateTime lastSent = DateTime.Now;
    private DateTime lastFullSent = DateTime.Now;

    private void Init()
    {
        timer.Every(60, TryFetch);
    }

    protected override void LoadDefaultConfig()
    {
        Log("Creating a new configuration file.");

        Config["URL"] = null;
        Config["SiteKey"] = null;
    }

    [Command("azlink.setup"), Permission("azlink.setup")]
    private void SetupCommand(IPlayer player, string command, string[] args)
    {
        if (args.Length < 2)
        {
            player.Reply(
                "You must first add this server in your Azuriom admin dashboard, in the 'Servers' section.");
            return;
        }

        Config["URL"] = args[0];
        Config["SiteKey"] = args[1];

        PingWebsite(() =>
        {
            player.Reply("Linked to the website successfully.");
            SaveConfig();
        }, code => player.Reply($"An error occurred, code {code}"));
    }

    [Command("azlink.status"), Permission("azlink.status")]
    private void StatusCommand(IPlayer player, string command, string[] args)
    {
        if (Config["URL"] == null)
        {
            player.Reply("AzLink is not configured yet, use the 'setup' subcommand first.");
            return;
        }

        PingWebsite(() => player.Reply("Connected to the website successfully."),
            code => player.Reply($"An error occurred, code {code}"));
    }

    [Command("azlink.fetch"), Permission("azlink.fetch")]
    private void FetchCommand(IPlayer player, string command, string[] args)
    {
        if (Config["URL"] == null)
        {
            player.Reply("AzLink is not configured yet, use the 'setup' subcommand first.");
            return;
        }

        RunFetch(res =>
        {
            DispatchCommands(res.Commands);

            player.Reply("Data has been fetched successfully.");
        }, code => player.Reply($"An error occurred, code {code}"), true);
    }

    [Command("azlink.money.set"), Permission("azlink.money")]
    private void MoneySetCommand(IPlayer player, string command, string[] args)
    {
        HandleMoneyCommand(player, "set", args);
    }

    [Command("azlink.money.add"), Permission("azlink.money")]
    private void MoneyAddCommand(IPlayer player, string command, string[] args)
    {
        HandleMoneyCommand(player, "add", args);
    }

    [Command("azlink.money.remove"), Permission("azlink.money")]
    private void MoneyRemoveCommand(IPlayer player, string command, string[] args)
    {
        HandleMoneyCommand(player, "remove", args);
    }

    private void HandleMoneyCommand(IPlayer player, string action, string[] args)
    {
        if (args.Length < 2)
        {
            player.Reply($"Usage: /azlink.money.{action} <player> <amount>");
            return;
        }

        var targetPlayer = covalence.Players.FindPlayer(args[0]);

        if (targetPlayer == null || !usersBySteamId.TryGetValue(targetPlayer.Id, out var user))
        {
            player.Reply($"Player '{args[0]}' not found.");
            return;
        }

        if (!double.TryParse(args[1], out var amount) || amount < 0)
        {
            player.Reply($"'{args[1]}' is not a valid amount.");
            return;
        }

        UpdateWebsiteBalance(player, user, action, amount);
    }

    private void TryFetch()
    {
        var url = (string?)Config["URL"];
        var siteKey = (string?)Config["SiteKey"];
        var now = DateTime.Now;

        if (url == null || siteKey == null)
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
            code => LogError("Unable to send data to the website (code {0})", code), full);
    }

    private void RunFetch(Action<FetchResponse> callback, Action<int> errorHandler, bool sendFullData)
    {
        var url = (string)Config["URL"];
        var body = JsonConvert.SerializeObject(GetServerData(sendFullData));

        webrequest.Enqueue($"{url}/api/azlink", body, (code, response) =>
        {
            if (code is < 200 or >= 300)
            {
                errorHandler(code);
                return;
            }

            var res = JsonConvert.DeserializeObject<FetchResponse>(response);

            foreach (var user in res.Users)
            {
                usersBySteamId[user.UserId] = user;
            }

            callback(res);
        }, this, RequestMethod.POST, GetRequestHeaders());
    }

    private void PingWebsite(Action onSuccess, Action<int> errorHandler)
    {
        var url = (string?)Config["URL"];
        var siteKey = (string?)Config["SiteKey"];

        if (url == null || siteKey == null)
        {
            throw new ApplicationException("AzLink is not configured yet.");
        }

        webrequest.Enqueue($"{url}/api/azlink", null, (code, _) =>
        {
            if (code is < 200 or >= 300)
            {
                errorHandler(code);
                return;
            }

            onSuccess();
        }, this, RequestMethod.GET, GetRequestHeaders());
    }

    private void UpdateWebsiteBalance(IPlayer sender, UserInfo user, string action, double amount)
    {
        var url = (string?)Config["URL"];
        var siteKey = (string?)Config["SiteKey"];

        if (url == null || siteKey == null)
        {
            throw new ApplicationException("AzLink is not configured yet.");
        }

        var body = JsonConvert.SerializeObject(new { amount });

        webrequest.Enqueue($"{url}/api/azlink/user/{user.Id}/money/{action}", body, (code, response) =>
        {
            if (code is < 200 or >= 300)
            {
                sender.Reply($"Unable to update the balance of {user.Name} (code {code})");
                return;
            }

            var res = JsonConvert.DeserializeObject<EditMoneyResult>(response);

            sender.Reply($"Successfully updated {user.Name}'s balance to {res.NewBalance}.");
        }, this, RequestMethod.POST, GetRequestHeaders());
    }

    private void DispatchCommands(ICollection<PendingCommand> commands)
    {
        if (commands.Count == 0)
        {
            return;
        }

        foreach (var info in commands)
        {
            var player = players.FindPlayerById(info.UserId);
            var name = player?.Name ?? info.UserName;

            foreach (var command in info.Values)
            {
                var cmd = command.Replace("{player}", name)
                    .Replace("{steam_id}", info.UserId);

                Log("Dispatching command to {0} ({1}): {2}", name, info.UserId, cmd);

                server.Command(cmd);
            }
        }

        Log("Dispatched commands to {0} players.", commands.Count);
    }

    private Dictionary<string, object> GetServerData(bool includeFullData)
    {
        var online = players.Connected.Select(player => new Dictionary<string, string>
        {
            { "name", player.Name }, { "uid", player.Id }
        });
        var data = new Dictionary<string, object>
        {
            {
                "platform", new Dictionary<string, string>
                {
                    { "type", "OXIDE" },
                    { "name", $"Oxide - {game}" },
                    { "version", server.Version },
                    { "key", "uid" }
                }
            },
            { "version", AzLinkVersion },
            { "players", online },
            { "maxPlayers", server.MaxPlayers },
            { "full", includeFullData }
        };

        if (includeFullData)
        {
            data.Add("ram", GC.GetTotalMemory(false) / 1024 / 1024);
        }

        return data;
    }

    private Dictionary<string, string> GetRequestHeaders()
    {
        return new Dictionary<string, string>
        {
            { "Azuriom-Link-Token", (string)Config["SiteKey"] },
            { "Accept", "application/json" },
            { "Content-type", "application/json" },
            { "User-Agent", $"AzLink Oxide v{AzLinkVersion}" }
        };
    }
}

class FetchResponse
{
    [JsonProperty("commands")] public List<PendingCommand> Commands { get; set; } = new();

    [JsonProperty("users")] public List<UserInfo> Users { get; set; } = new();
}

class PendingCommand
{
    [JsonProperty("uid")] public string UserId { get; set; } = "";

    [JsonProperty("name")] public string UserName { get; set; } = "";

    [JsonProperty("values")] public List<string> Values { get; set; } = new();
}

class UserInfo
{
    [JsonProperty("id")] public string Id { get; set; } = "";

    [JsonProperty("uid")] public string UserId { get; set; } = "";

    [JsonProperty("name")] public string Name { get; set; } = "";

    [JsonProperty("money")] public float UserBalance { get; set; } = 0;
}

class EditMoneyResult
{
    [JsonProperty("new_balance")] public double NewBalance { get; set; } = 0;
}